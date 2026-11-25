using System;
using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace ProjectSoullike
{
    // 보스 디렉터(강화학습, PPO). 보스가 다음 패턴을 고르는 순간(IBossPatternPolicy.Choose) 결정을 한 번 내린다.
    //   행동 ① 다음 패턴: 패턴 표의 고를 수 있는 패턴 중 하나. 그 순간 후보(페이즈·거리·쿨타임으로 이미 걸러진 것)만 열어 둔다.
    //   행동 ② 이 패턴이 끝난 뒤 다음 패턴까지 대기: 0.4·0.7·1.0·1.4·1.8초(난이도 손잡이 범위 안 5단계).
    // 전조·판정·피해는 건드리지 않는다. 고른 대기에서 점수의 압박 배율(0.7~1.5)이 나온다(IBossPressureSource).
    // 결정은 Academy.EnvironmentStep()으로 그 자리에서 받는다(학습기 연결·ONNX 추론 둘 다 같은 길).
    // 보상(PLAN_SCORE_1116): 접전(두 체력이 함께 줄어듦) +, 아슬아슬한 결말 +, 일방적 전개 −, 같은 패턴 3연속 −.
    public sealed class BossDirectorAgent : Agent, IBossPatternPolicy, IBossPacingPolicy, IBossPressureSource
    {
        public const string BehaviorName = "BossDirector";
        public static readonly float[] Intervals = { 0.4f, 0.7f, 1.0f, 1.4f, 1.8f };
        public const int ObservationSize = 7 + 2 * 19 + 4 + 4 + 2;

        // 행동 ①의 순서. 페이즈 전환(300)은 강제 발동이라 정책을 거치지 않으므로 뺀다.
        public static readonly int[] PatternIds = BuildPatternIds();
        private static readonly Dictionary<int, int> s_indexOf = BuildIndex();

        private BossController _boss;
        private PlayerHealth _player;
        private PlayerParry _parry;
        private KeyboardPlayerController _controller;
        private BossPatternContext _context;
        private BossPatternSpec _chosen;
        private float _nextInterval = -1f;
        private int _lastPatternIndex = -1;
        private int _repeat;
        private int _lastIntervalIndex = 1;
        private readonly Queue<float> _recentPressure = new Queue<float>();
        private readonly Queue<BossStrikeOutcome> _recentStrikes = new Queue<BossStrikeOutcome>();
        private float _fightStart;
        private int _playerAttacks, _playerEvades, _playerParries;
        private bool _failed;
        private readonly Dictionary<int, int> _fightPatternCounts = new Dictionary<int, int>();

        // v2 보상: 한 판에서 같은 패턴이 결정의 이 비율을 넘으면 그때마다 감점(패턴 다양성). 0이면 끔(v1).
        // 학습 실행 인자 -patternShareCap 0.3, -patternSharePenalty 0.05
        public static float PatternShareCap = 0f;
        public static float PatternSharePenalty = 0.05f;

        public bool InFight => _boss != null;
        public int Decisions { get; private set; }
        public int RepeatPenalties { get; private set; }

        public float ScorePressure
        {
            get
            {
                if (_recentPressure.Count == 0) return CombatScore.PressureDefault;
                float sum = 0f;
                foreach (float p in _recentPressure) sum += p;
                return sum / _recentPressure.Count;
            }
        }

        private static int[] BuildPatternIds()
        {
            var ids = new List<int>();
            foreach (BossPatternSpec spec in BossPatternTable.Patterns)
                if (spec.Slot != BossPatternSlot.PhaseShift) ids.Add(spec.Id);
            return ids.ToArray();
        }

        private static Dictionary<int, int> BuildIndex()
        {
            var map = new Dictionary<int, int>();
            for (int i = 0; i < PatternIds.Length; i++) map[PatternIds[i]] = i;
            return map;
        }

        // 대기 시간 → 압박 배율. 기본 대기(난이도 손잡이 기본값 0.7초, 고정 보스와 같음) = 1.0,
        // 가장 짧은 0.4초 = 1.5, 가장 긴 1.8초 = 0.7. 그 사이는 직선으로 잇는다.
        public const float DefaultInterval = 0.7f;
        public static float PressureFor(float interval)
        {
            float lo = Intervals[0], hi = Intervals[Intervals.Length - 1];
            float pressure = interval <= DefaultInterval
                ? Mathf.Lerp(CombatScore.PressureMax, CombatScore.PressureDefault, Mathf.InverseLerp(lo, DefaultInterval, interval))
                : Mathf.Lerp(CombatScore.PressureDefault, CombatScore.PressureMin, Mathf.InverseLerp(DefaultInterval, hi, interval));
            return CombatScore.ClampPressure(pressure);
        }

        // BehaviorParameters를 맞춘 디렉터를 만든다. model이 있으면 추론, 없으면 학습기 연결(없으면 휴리스틱).
        public static BossDirectorAgent Create(Transform parent, Unity.InferenceEngine.ModelAsset model, bool inferenceOnly)
        {
            var go = new GameObject("BossDirector");
            go.SetActive(false);
            if (parent != null) go.transform.SetParent(parent, false);
            var parameters = go.AddComponent<BehaviorParameters>();
            parameters.BehaviorName = BehaviorName;
            parameters.BrainParameters.VectorObservationSize = ObservationSize;
            parameters.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(PatternIds.Length, Intervals.Length);
            parameters.Model = model;
            parameters.BehaviorType = inferenceOnly ? BehaviorType.InferenceOnly : BehaviorType.Default;
            var agent = go.AddComponent<BossDirectorAgent>();
            agent.MaxStep = 0;
            go.SetActive(true);
            return agent;
        }

        public void BeginFight(BossController boss, PlayerHealth player)
        {
            EndFightHooks();
            _boss = boss;
            _player = player;
            _parry = player != null ? player.GetComponent<PlayerParry>() : null;
            _controller = player != null ? player.GetComponent<KeyboardPlayerController>() : null;
            _boss.StrikeResolved += OnStrikeResolved;
            if (_parry != null) _parry.Parried += OnParried;
            if (_player != null) _player.DamageEvaded += OnEvaded;
            if (_controller != null) _controller.AttackStarted += OnPlayerAttack;
            _fightStart = Time.time;
            _lastPatternIndex = -1;
            _repeat = 0;
            _lastIntervalIndex = 1;
            _recentPressure.Clear();
            _recentStrikes.Clear();
            _playerAttacks = _playerEvades = _playerParries = 0;
            _nextInterval = -1f;
            _failed = false;
            _fightPatternCounts.Clear();
            Decisions = 0;
            RepeatPenalties = 0;
            BossController.PatternPolicy = this;
        }

        // 판이 끝났을 때. outcome: 1 = 플레이어 승리(보스 격파), -1 = 플레이어 사망, 0 = 시간 초과·중단.
        public void EndFight(int outcome, float playerHealthRatio, float bossHealthRatio, bool timedOut)
        {
            if (Decisions > 0)
            {
                AddReward(FinalReward(outcome, playerHealthRatio, bossHealthRatio, timedOut));
                EndEpisode();
            }
            EndFightHooks();
            if (ReferenceEquals(BossController.PatternPolicy, this)) BossController.PatternPolicy = null;
        }

        public static float FinalReward(int outcome, float playerHealthRatio, float bossHealthRatio, bool timedOut)
        {
            if (timedOut) return -0.5f;
            if (outcome > 0)
            {
                if (playerHealthRatio < 0.35f) return 1f;    // 아슬아슬하게 이김
                if (playerHealthRatio > 0.8f) return -1f;    // 보스가 너무 약했다
                return 0.3f;
            }
            if (outcome < 0)
            {
                if (bossHealthRatio < 0.3f) return 0.7f;     // 거의 이길 뻔하고 짐
                if (bossHealthRatio > 0.7f) return -1f;      // 보스가 너무 셌다
                return 0f;
            }
            return 0f;
        }

        private void EndFightHooks()
        {
            if (_boss != null) _boss.StrikeResolved -= OnStrikeResolved;
            if (_parry != null) _parry.Parried -= OnParried;
            if (_player != null) _player.DamageEvaded -= OnEvaded;
            if (_controller != null) _controller.AttackStarted -= OnPlayerAttack;
            _boss = null; _player = null; _parry = null; _controller = null; _context = null;
        }

        private void OnDisable()
        {
            EndFightHooks();
            if (ReferenceEquals(BossController.PatternPolicy, this)) BossController.PatternPolicy = null;
        }

        private void OnStrikeResolved(BossPatternSpec spec, BossStrikeInfo info, int step, BossStrikeOutcome outcome)
        {
            _recentStrikes.Enqueue(outcome);
            while (_recentStrikes.Count > 8) _recentStrikes.Dequeue();
        }

        private void OnParried(DamageInfo _) => _playerParries++;
        private void OnEvaded(DamageInfo _) => _playerEvades++;
        private void OnPlayerAttack(PlayerAttackDefinition _) => _playerAttacks++;

        // ── IBossPatternPolicy ─────────────────────────────────────
        public BossPatternSpec Choose(BossPatternContext context)
        {
            if (_failed || _boss == null || context.Boss != _boss || context.Candidates.Count == 0) return null;
            try
            {
                _context = context;
                _chosen = null;
                // 접전 보상: 두 체력이 비슷하게 줄어들고 있으면 +, 한쪽으로 크게 기울면 −.
                float gap = Mathf.Abs(context.PlayerHealthRatio - context.BossHealthRatio);
                AddReward(gap < 0.25f ? 0.02f : -0.01f);
                RequestDecision();
                Academy.Instance.EnvironmentStep();
                Decisions++;
                return _chosen;
            }
            catch (Exception error)
            {
                // 추론이 실패하면 이 판은 기본 추첨으로 돌아간다(게임이 멈추면 안 된다).
                _failed = true;
                Debug.LogWarning("[Boss Director] decision failed, falling back to default patterns: " + error.Message, this);
                return null;
            }
            finally
            {
                _context = null;
            }
        }

        // ── IBossPacingPolicy ──────────────────────────────────────
        public float NextPatternInterval(BossController boss, float defaultInterval)
        {
            if (_failed || boss != _boss || _nextInterval < 0f) return -1f;
            float interval = _nextInterval;
            _nextInterval = -1f;
            return interval;
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            BossPatternContext c = _context;
            float fight = _boss != null ? Time.time - _fightStart : 0f;
            sensor.AddObservation(c != null ? c.PlayerHealthRatio : 1f);
            sensor.AddObservation(c != null ? c.BossHealthRatio : 1f);
            sensor.AddObservation(c != null ? Mathf.Clamp01(c.Distance / 20f) : 0f);
            sensor.AddObservation(c != null && c.Phase == 2 ? 1f : 0f);
            sensor.AddObservation(c != null ? c.Segment / 3f : 0f);
            sensor.AddObservation(c != null && c.FromChase ? 1f : 0f);
            sensor.AddObservation(Mathf.Clamp01(fight / CombatScore.TimeLimitSeconds));

            // 지금 고를 수 있는 패턴(19)과 바로 전 패턴(19)
            for (int i = 0; i < PatternIds.Length; i++)
                sensor.AddObservation(c != null && IndexIn(c, PatternIds[i]) ? 1f : 0f);
            for (int i = 0; i < PatternIds.Length; i++)
                sensor.AddObservation(i == _lastPatternIndex ? 1f : 0f);

            // 최근 8타의 결과 비율: 맞음·회피·패링·빗나감
            float n = Mathf.Max(1, _recentStrikes.Count);
            int hit = 0, evaded = 0, parried = 0, missed = 0;
            foreach (BossStrikeOutcome o in _recentStrikes)
            {
                switch (o)
                {
                    case BossStrikeOutcome.Hit: hit++; break;
                    case BossStrikeOutcome.Evaded: evaded++; break;
                    case BossStrikeOutcome.Parried: parried++; break;
                    default: missed++; break;
                }
            }
            sensor.AddObservation(hit / n);
            sensor.AddObservation(evaded / n);
            sensor.AddObservation(parried / n);
            sensor.AddObservation(missed / n);

            // 플레이어 버릇 요약: 공격 빈도(10초당), 무적 회피·패링 성공 비중, 실력 단서(받은 공격 중 맞은 비율)
            float minutes = Mathf.Max(10f, fight) / 10f;
            int defended = _playerEvades + _playerParries;
            sensor.AddObservation(Mathf.Clamp01(_playerAttacks / minutes / 10f));
            sensor.AddObservation(defended > 0 ? _playerEvades / (float)defended : 0.5f);
            sensor.AddObservation(defended > 0 ? _playerParries / (float)defended : 0.5f);
            sensor.AddObservation(_recentStrikes.Count > 0 ? hit / n : 0.5f);

            sensor.AddObservation(_lastIntervalIndex / (float)(Intervals.Length - 1));
            sensor.AddObservation(Mathf.Clamp01(_repeat / 3f));
        }

        private static bool IndexIn(BossPatternContext c, int id)
        {
            for (int i = 0; i < c.Candidates.Count; i++) if (c.Candidates[i].Id == id) return true;
            return false;
        }

        public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
        {
            BossPatternContext c = _context;
            if (c == null) return;
            bool any = false;
            for (int i = 0; i < PatternIds.Length; i++)
            {
                bool open = IndexIn(c, PatternIds[i]);
                any |= open;
                actionMask.SetActionEnabled(0, i, open);
            }
            if (!any)
                for (int i = 0; i < PatternIds.Length; i++) actionMask.SetActionEnabled(0, i, true);
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            BossPatternContext c = _context;
            if (c == null) return;
            int patternIndex = actions.DiscreteActions[0];
            int intervalIndex = Mathf.Clamp(actions.DiscreteActions[1], 0, Intervals.Length - 1);

            BossPatternSpec chosen = null;
            if (patternIndex >= 0 && patternIndex < PatternIds.Length)
            {
                int id = PatternIds[patternIndex];
                for (int i = 0; i < c.Candidates.Count; i++)
                    if (c.Candidates[i].Id == id) { chosen = c.Candidates[i]; break; }
            }
            if (chosen == null)
            {
                AddReward(-0.05f);
                chosen = c.Candidates[0];
            }

            int chosenIndex = s_indexOf.TryGetValue(chosen.Id, out int idx) ? idx : -1;
            _repeat = chosenIndex == _lastPatternIndex ? _repeat + 1 : 0;
            if (_repeat >= 2) { AddReward(-0.2f); RepeatPenalties++; } // 같은 패턴 3연속
            _lastPatternIndex = chosenIndex;
            _fightPatternCounts.TryGetValue(chosen.Id, out int count);
            _fightPatternCounts[chosen.Id] = ++count;
            int decided = Decisions + 1;
            if (PatternShareCap > 0f && decided >= 6 && count / (float)decided > PatternShareCap) AddReward(-PatternSharePenalty);

            _chosen = chosen;
            _lastIntervalIndex = intervalIndex;
            _nextInterval = Intervals[intervalIndex];
            _recentPressure.Enqueue(PressureFor(_nextInterval));
            while (_recentPressure.Count > 3) _recentPressure.Dequeue();
        }

        // 학습기·모델 없이 돌릴 때: 기본 비중 추첨과 기본 대기(0.7초)를 흉내 낸다.
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var discrete = actionsOut.DiscreteActions;
            BossPatternContext c = _context;
            int pick = 0;
            if (c != null && c.Candidates.Count > 0)
            {
                float total = 0f;
                for (int i = 0; i < c.Weights.Count; i++) total += c.Weights[i];
                float roll = UnityEngine.Random.value * Mathf.Max(total, 1e-4f);
                BossPatternSpec spec = c.Candidates[c.Candidates.Count - 1];
                for (int i = 0; i < c.Candidates.Count; i++)
                {
                    roll -= c.Weights[i];
                    if (roll <= 0f) { spec = c.Candidates[i]; break; }
                }
                pick = s_indexOf.TryGetValue(spec.Id, out int idx) ? idx : 0;
            }
            discrete[0] = pick;
            discrete[1] = 1;
        }
    }
}
