using System.Globalization;
using UnityEngine;

namespace ProjectSoullike
{
    // 패턴 디렉터가 "지금 얼마나 몰아치고 있는지"를 점수에 알려 주는 훅.
    // BossController.PatternPolicy가 이것도 구현하면 보스 공격을 넘길 때마다 이 값을 압박 배율로 쓴다(0.7~1.5로 자름).
    // 디렉터가 없거나 구현하지 않으면 1.0.
    public interface IBossPressureSource
    {
        float ScorePressure { get; }
    }

    // 단일 난이도 점수(PLAN_SCORE_1116 확정 공식): 한 판의 결과를 점수와 등급으로 바꾼다.
    //   SCORE = 보스 세기 + 기술 + 시간 + 격파
    //   보스 세기  보스 타격 하나가 플레이어를 맞히지 못하고 끝날 때마다(회피·패링·빗나감) 100 × 패턴 가중치 × 압박 배율.
    //              타격 결과는 BossController.StrikeResolved(전투 로그 boss_strike와 같은 판정)를 그대로 쓴다.
    //   기술       패링 150 · 반격 적중 500 · 체간 붕괴 400 · 커맨드 공격 적중 60 · 무적 회피 50
    //   시간       승리만: (240초 − 전투 시간) × 25, 0 미만은 0. 전투 시간 = 보스와 처음 맞붙은 순간부터
    //   격파       승리 +5,000, 패배 0
    // 최고 점수는 PlayerPrefs에 남긴다(배치 모드에서는 남기지 않는다).
    public sealed class CombatScore
    {
        public const int StrikeBasePoints = 100;
        public const float PressureMin = 0.7f, PressureMax = 1.5f, PressureDefault = 1f;
        public const int ParryPoints = 150;
        public const int RipostePoints = 500;
        public const int StanceBreakPoints = 400;
        public const int CommandHitPoints = 60;
        public const int EvadePoints = 50;
        public const float TimeLimitSeconds = 240f;
        public const int TimePointsPerSecond = 25;
        public const int VictoryPoints = 5000;

        // 등급 하한(11/18, 봇 평가로 다시 잡음). 보스 디렉터 v2 상대 규칙 봇 3단계 × 100판의 점수 분포:
        //   초보 중앙값 2,104(10~90% 1,266~3,713) · 중급 11,882(3,217~13,727) · 숙련 13,956(6,600~15,244).
        //   A 하한 = 숙련 봇 중앙값(기획 원칙), S = 숙련 봇 상위 약 10%, B = 중급 중앙값 아래, C = 초보 상위 약 30%.
        // 사람 테스트(11/20~21) 기록으로 다시 보정한다.
        public const int RankS = 15500, RankA = 13900, RankB = 9000, RankC = 3000;
        private const string BestKey = "soullike.score.best";

        public readonly struct Result
        {
            public Result(int bossStrength, int skill, int time, int clear)
            {
                BossStrength = bossStrength; Skill = skill; Time = time; Clear = clear;
                Total = bossStrength + skill + time + clear;
                Rank = RankOf(Total);
            }

            public int BossStrength { get; }
            public int Skill { get; }
            public int Time { get; }
            public int Clear { get; }
            public int Total { get; }
            public string Rank { get; }
        }

        public float BossStrength { get; private set; }
        public int StrikesAvoided { get; private set; }
        public int Parries { get; private set; }
        public int Ripostes { get; private set; }
        public int StanceBreaks { get; private set; }
        public int Evades { get; private set; }
        public int CommandHits { get; private set; }
        public int HitsTaken { get; private set; }
        public float FightStartTime { get; private set; } = -1f;

        private PlayerHealth _player;
        private PlayerParry _parry;
        private GolemHealth _boss;
        private BossController _bossController;

        public static string RankOf(int total) =>
            total >= RankS ? "S" : total >= RankA ? "A" : total >= RankB ? "B" : total >= RankC ? "C" : "D";

        public static float ClampPressure(float pressure) =>
            float.IsNaN(pressure) || float.IsInfinity(pressure) ? PressureDefault : Mathf.Clamp(pressure, PressureMin, PressureMax);

        // 지금 압박 배율. 디렉터(IBossPatternPolicy)가 IBossPressureSource도 구현하면 그 값, 아니면 1.0.
        public static float CurrentPressure() =>
            BossController.PatternPolicy is IBossPressureSource source ? ClampPressure(source.ScorePressure) : PressureDefault;

        // 보스 타격 하나의 보스 세기 점수. 맞았으면 0.
        public static float StrikePoints(int patternId, BossStrikeOutcome outcome, float pressure)
        {
            if (outcome == BossStrikeOutcome.Hit) return 0f;
            return StrikeBasePoints * ScorePatternWeights.For(patternId) * ClampPressure(pressure);
        }

        public static Result Compute(bool victory, float fightSeconds, float bossStrength,
            int parries, int ripostes, int stanceBreaks, int evades, int commandHits)
        {
            int boss = Mathf.RoundToInt(Mathf.Max(0f, bossStrength));
            int skill = parries * ParryPoints + ripostes * RipostePoints + stanceBreaks * StanceBreakPoints +
                commandHits * CommandHitPoints + evades * EvadePoints;
            int time = victory ? Mathf.RoundToInt(Mathf.Max(0f, TimeLimitSeconds - fightSeconds) * TimePointsPerSecond) : 0;
            int clear = victory ? VictoryPoints : 0;
            return new Result(boss, skill, time, clear);
        }

        public float FightSeconds(float endTime) => FightStartTime >= 0f ? Mathf.Max(0f, endTime - FightStartTime) : 0f;

        public Result Finish(bool victory, float endTime) =>
            Compute(victory, FightSeconds(endTime), BossStrength, Parries, Ripostes, StanceBreaks, Evades, CommandHits);

        public void Attach(PlayerHealth player, GolemHealth boss)
        {
            Detach();
            _player = player;
            _boss = boss;
            _parry = player != null ? player.GetComponent<PlayerParry>() : null;
            _bossController = boss != null ? boss.GetComponent<BossController>() : null;
            if (_player != null) { _player.DamageTaken += OnPlayerHit; _player.DamageEvaded += OnPlayerEvaded; }
            if (_parry != null) _parry.Parried += OnParried;
            if (_boss != null) _boss.DamageTaken += OnBossHit;
            if (_bossController != null)
            {
                _bossController.StanceBroken += OnStanceBroken;
                _bossController.AttackStarted += MarkFight;
                _bossController.StrikeResolved += OnStrikeResolved;
            }
        }

        public void Detach()
        {
            if (_player != null) { _player.DamageTaken -= OnPlayerHit; _player.DamageEvaded -= OnPlayerEvaded; }
            if (_parry != null) _parry.Parried -= OnParried;
            if (_boss != null) _boss.DamageTaken -= OnBossHit;
            if (_bossController != null)
            {
                _bossController.StanceBroken -= OnStanceBroken;
                _bossController.AttackStarted -= MarkFight;
                _bossController.StrikeResolved -= OnStrikeResolved;
            }
            _player = null; _parry = null; _boss = null; _bossController = null;
        }

        // 전투 시간은 보스와 처음 맞붙은 순간(보스의 첫 공격 전조 또는 첫 피해·회피·패링)부터 잰다.
        // 다리를 걷고 입장 연출을 보는 시간은 빠진다.
        private void MarkFight()
        {
            if (FightStartTime < 0f) FightStartTime = UnityEngine.Time.time;
        }

        private void OnStrikeResolved(BossPatternSpec spec, BossStrikeInfo info, int step, BossStrikeOutcome outcome)
        {
            MarkFight();
            if (outcome == BossStrikeOutcome.Hit) return;
            StrikesAvoided++;
            BossStrength += StrikePoints(spec != null ? spec.Id : 0, outcome, CurrentPressure());
        }

        private void OnPlayerHit(DamageInfo damage)
        {
            MarkFight();
            if (damage.Amount > 0f) HitsTaken++;
        }

        private void OnPlayerEvaded(DamageInfo _) { MarkFight(); Evades++; }
        private void OnParried(DamageInfo _) { MarkFight(); Parries++; }
        private void OnStanceBroken() => StanceBreaks++;

        private void OnBossHit(DamageInfo damage)
        {
            MarkFight();
            if (damage.AttackIndex == PlayerAttackDefinition.RiposteAttackIndex) Ripostes++;
            else if (damage.AttackIndex >= CombatTelemetryRecorder.FirstCommandAttackIndex) CommandHits++;
        }

        public static int Best => PlayerPrefs.GetInt(BestKey, 0);

        // 최고 점수를 넘으면 남기고 true. 배치 모드(자동 검증)에서는 남기지 않는다.
        public static bool SubmitBest(int total)
        {
            if (total <= Best) return false;
            if (Application.isBatchMode) return true;
            PlayerPrefs.SetInt(BestKey, total);
            PlayerPrefs.Save();
            return true;
        }

        public static string Format(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
