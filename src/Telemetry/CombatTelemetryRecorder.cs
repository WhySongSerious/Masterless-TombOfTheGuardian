using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectSoullike
{
    // ML 동적 난이도(#22) 학습 데이터 수집의 첫 단계: 전투 이벤트와 시도별 요약을 파일로 남긴다.
    // 씬 하나를 불러올 때마다 시도(attempt) 하나로 보고, 결과가 나기 전에 씬을 떠나거나 종료하면 aborted로 기록한다.
    // 출력: Application.persistentDataPath/Telemetry/<sessionId>/events.jsonl, attempts.csv
    // 참가자 ID는 실행 인자 -participant P01 로 넣는다. 없으면 "anon". 이름·연락처는 기록하지 않는다.
    [DisallowMultipleComponent]
    public sealed class CombatTelemetryRecorder : MonoBehaviour
    {
        // 2: 보스 패턴·패링·회피 이벤트, 시도별 난이도 손잡이 값 열 추가(#25).
        // 3: 페이즈·구간과 패턴 ID(101~303), 타격별 결과(boss_strike: hit/evaded/parried/missed), 수집 판 표시,
        //    근접 비중 손잡이 3개(combo_probability 대신)·도약·메인 쿨타임 열.
        // 4: 플레이어 공격 이름(move) 열, 공격을 낼 때마다 player_attack, 킥에 밀려나면 boss_shoved 이벤트,
        //    시도별 player_attacks·player_moves_used(낸 공격 종류 수)·player_command_hits(커맨드 공격 적중) 열.
        // 5: 점수(PLAN_SCORE_1116). boss_strike에 scoreWeight·scorePressure·scorePoints(보스 세기 점수, 맞았으면 0),
        //    시도별 score_* 열(승리·패배 판만, 그 밖은 빈칸)을 맨 뒤에 붙였다. 4까지의 열·필드는 그대로라 기존 분석이 그대로 돈다.
        // 6: boss_strike에 distance(그 순간 보스-플레이어 거리, m) — 패턴 난이도 회귀(패턴·거리·페이즈·실력)용. 다른 이벤트는 0.
        public const int SchemaVersion = 6;
        // 이 순번(공격 목록 1부터)부터가 커맨드 공격(구르기 베기·돌진 찌르기·후퇴 베기·차지 강공·킥).
        public const int FirstCommandAttackIndex = 9;
        private const string LogPrefix = "[Combat Telemetry]";

        // 학습 조건이 생기기 전까지는 고정 난이도 수집만 한다. 적응 조건은 #25/#26에서 채운다.
        [SerializeField] private string condition = "fixed";
        [SerializeField] private string difficulty = "default";
        [SerializeField] private bool writeFiles = true;

        private static string s_sessionId;
        private static string s_participantId;
        private static string s_directory;
        private static int s_attempt;

        private PlayerHealth _player;
        private GolemHealth _boss;
        private BossController _bossController;
        private PlayerParry _playerParry;
        private KeyboardPlayerController _playerController;
        private readonly System.Collections.Generic.HashSet<string> _movesUsed = new System.Collections.Generic.HashSet<string>();
        private AttemptSummary _summary;
        private bool _finished;
        // 이 시도가 시작된 시각(씬을 다시 부르지 않고 판을 이어 가는 학습 결투장에서도 시도 길이가 맞게).
        private float _startedAt;
        // 결과 화면과 같은 규칙으로 점수를 따로 센다(CombatFlowController와 같은 CombatScore).
        private readonly CombatScore _score = new CombatScore();

        public static string SessionId => s_sessionId;
        // 학습·자동 평가가 판마다 참가자(봇 단계)와 조건(fixed/rl 등)을 바꿀 때 쓴다. null이면 실행 인자·인스펙터 값.
        public static string ParticipantOverride { get; set; }
        public static string ConditionOverride { get; set; }
        public static string OutputDirectory => s_directory;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallOnFirstScene()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureInstance();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureInstance();
        }

        private static void EnsureInstance()
        {
            if (FindAnyObjectByType<CombatTelemetryRecorder>() != null) return;
            if (FindAnyObjectByType<PlayerHealth>() == null || FindAnyObjectByType<GolemHealth>() == null) return;

            new GameObject(nameof(CombatTelemetryRecorder)).AddComponent<CombatTelemetryRecorder>();
        }

        private void Start()
        {
            EnsureSession();

            _player = FindAnyObjectByType<PlayerHealth>();
            _boss = FindAnyObjectByType<GolemHealth>();
            _player.DamageTaken += OnPlayerDamaged;
            _player.Died += OnPlayerDied;
            _player.DamageEvaded += OnPlayerEvaded;
            _boss.DamageTaken += OnBossDamaged;
            _boss.Died += OnBossDied;
            _bossController = _boss.GetComponent<BossController>();
            _playerParry = _player.GetComponent<PlayerParry>();
            if (_playerParry != null) _playerParry.Failed += OnParryFailed;
            _playerController = _player.GetComponent<KeyboardPlayerController>();
            if (_playerController != null) _playerController.AttackStarted += OnPlayerAttackStarted;
            if (_bossController != null)
            {
                _bossController.Shoved += OnBossShoved;
                _bossController.PatternSpecStarted += OnBossPattern;
                _bossController.StrikeParried += OnBossParried;
                _bossController.StrikeResolved += OnBossStrikeResolved;
                _bossController.SegmentChanged += OnBossSegmentChanged;
                _bossController.CalibrationCleared += OnCalibrationCleared;
            }

            _score.Attach(_player, _boss);

            s_attempt++;
            _startedAt = Time.timeSinceLevelLoad;
            _summary = new AttemptSummary
            {
                schemaVersion = SchemaVersion,
                buildVersion = Application.version,
                sessionId = s_sessionId,
                participantId = ParticipantOverride ?? s_participantId,
                attempt = s_attempt,
                scene = SceneManager.GetActiveScene().name,
                condition = ConditionOverride ?? condition,
                difficulty = difficulty,
                startedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                outcome = "in_progress",
                playerMaxHealth = _player.MaxHealth,
                bossMaxHealth = _boss.MaxHealth,
            };
            // 이 판에 고정된 난이도 값. 수호자 패턴을 쓰지 않는 보스면 비워 둔다.
            if (_bossController != null && _bossController.UseGuardianPatterns) _summary.tuning = _bossController.Tuning;

            WriteEvent("attempt_start", 0f, 0, null);
            // 정상 수련장은 재시작마다 시간대(밝기)가 바뀌므로 난이도 분석에서 따로 볼 수 있게 남긴다. amount = 순번, pattern = 이름.
            if (SummitTimeOfDay.CurrentName != null) WriteEvent("time_of_day", SummitTimeOfDay.CurrentIndex, 0, null, SummitTimeOfDay.CurrentName);
        }

        private void OnDestroy()
        {
            if (_playerParry != null) _playerParry.Failed -= OnParryFailed;
            if (_playerController != null) _playerController.AttackStarted -= OnPlayerAttackStarted;
            if (_player != null)
            {
                _player.DamageTaken -= OnPlayerDamaged;
                _player.DamageEvaded -= OnPlayerEvaded;
                _player.Died -= OnPlayerDied;
            }

            if (_bossController != null)
            {
                _bossController.Shoved -= OnBossShoved;
                _bossController.PatternSpecStarted -= OnBossPattern;
                _bossController.StrikeParried -= OnBossParried;
                _bossController.StrikeResolved -= OnBossStrikeResolved;
                _bossController.SegmentChanged -= OnBossSegmentChanged;
                _bossController.CalibrationCleared -= OnCalibrationCleared;
            }

            if (_boss != null)
            {
                _boss.DamageTaken -= OnBossDamaged;
                _boss.Died -= OnBossDied;
            }

            // 결과 없이 씬을 떠나면(재시작·메뉴 이동) 중도 종료로 남긴다.
            Finish("aborted");
            _score.Detach();
        }

        private void OnApplicationQuit()
        {
            Finish("aborted");
        }

        private void OnPlayerDamaged(DamageInfo info)
        {
            _summary.playerHitsTaken++;
            _summary.playerDamageTaken += info.Amount;
            WriteEvent("player_hit", info.Amount, info.AttackIndex, info.Source, CurrentBossPattern(), CurrentBossPatternId(),
                _bossController != null ? _bossController.CurrentStrikeId : 0);
        }

        // 구르기 무적 시간에 보스 공격이 닿은 경우(회피 성공).
        private void OnPlayerEvaded(DamageInfo info)
        {
            _summary.playerEvades++;
            WriteEvent("player_evade", info.Amount, info.AttackIndex, info.Source, CurrentBossPattern(), CurrentBossPatternId(),
                _bossController != null ? _bossController.CurrentStrikeId : 0);
        }

        // attackIndex = 이번에 칠 타수, pattern = 종류 이름, patternId = 시트 ID(101~303).
        private void OnBossPattern(BossPatternSpec spec, int stepCount)
        {
            _summary.bossPatterns++;
            WriteEvent("boss_pattern", 0f, stepCount, _bossController.gameObject, spec.Kind.ToString(), spec.Id);
        }

        // 타격 하나의 결과. amount = 타격 피해량(고정값), attackIndex = 패턴 안 순번, outcome = hit/evaded/parried/missed.
        private void OnBossStrikeResolved(BossPatternSpec spec, BossStrikeInfo info, int step, BossStrikeOutcome outcome)
        {
            _summary.bossStrikes++;
            switch (outcome)
            {
                case BossStrikeOutcome.Hit: _summary.bossStrikesHit++; break;
                case BossStrikeOutcome.Evaded: _summary.bossStrikesEvaded++; break;
                case BossStrikeOutcome.Parried: _summary.bossStrikesParried++; break;
                default: _summary.bossStrikesMissed++; break;
            }
            float damage = _bossController.CurrentStrikeId == info.Id && _bossController.CurrentStrike != null
                ? _bossController.CurrentStrike.damage : _bossController.GetStrikeSettings(info.Id).damage;
            int patternId = spec?.Id ?? 0;
            float pressure = CombatScore.CurrentPressure();
            WriteEvent("boss_strike", damage, step, _bossController.gameObject, spec?.Kind.ToString() ?? "",
                patternId, info.Id, outcome.ToString().ToLowerInvariant(),
                scoreWeight: ScorePatternWeights.For(patternId), scorePressure: pressure,
                scorePoints: CombatScore.StrikePoints(patternId, outcome, pressure),
                distance: _player != null ? Vector3.Distance(_bossController.transform.position, _player.transform.position) : 0f);
        }

        private void OnBossSegmentChanged(int phase, int segment)
        {
            _summary.bossPhaseReached = Mathf.Max(_summary.bossPhaseReached, phase);
            _summary.bossSegmentReached = phase * 10 + segment;
            WriteEvent("boss_segment", phase, segment, _bossController.gameObject);
        }

        private void OnCalibrationCleared() => Finish("calibration_cleared");

        private void OnBossParried(BossPattern pattern, int step)
        {
            _summary.playerParries++;
            WriteEvent("player_parry", 0f, step, _bossController.gameObject, pattern.ToString(), CurrentBossPatternId(),
                _bossController.CurrentStrikeId);
        }

        private void OnParryFailed(string reason)
        {
            if (_summary != null && !_finished)
                WriteEvent("player_parry_failed_" + reason, 0f, 0, null, CurrentBossPattern(), CurrentBossPatternId());
        }

        private int CurrentBossPatternId() =>
            _bossController != null && _bossController.CurrentPattern != BossPattern.None ? _bossController.CurrentPatternId : 0;

        private string CurrentBossPattern()
        {
            return _bossController != null && _bossController.CurrentPattern != BossPattern.None
                ? _bossController.CurrentPattern.ToString() : "";
        }

        private void OnBossDamaged(DamageInfo info)
        {
            _summary.bossHitsTaken++;
            _summary.bossDamageTaken += info.Amount;
            if (info.AttackIndex >= FirstCommandAttackIndex && info.AttackIndex != PlayerAttackDefinition.RiposteAttackIndex)
                _summary.playerCommandHits++;
            WriteEvent("boss_hit", info.Amount, info.AttackIndex, info.Source,
                move: _playerController != null ? _playerController.AttackNameAt(info.AttackIndex) : "");
        }

        // 플레이어가 공격을 낼 때마다(빗나가도). 차지 강공은 놓을 때 한 번. amount = 공격 피해량, attackIndex = 공격 목록 순번(반격 100).
        private void OnPlayerAttackStarted(PlayerAttackDefinition attack)
        {
            if (_summary == null || _finished) return;
            _summary.playerAttacks++;
            _movesUsed.Add(attack.name);
            _summary.playerMovesUsed = _movesUsed.Count;
            WriteEvent("player_attack", attack.damage, _playerController.AttackIndexOf(attack), _player.gameObject,
                CurrentBossPattern(), CurrentBossPatternId(), move: attack.name);
        }

        // 킥에 밀려났을 때. amount = 밀린 거리(m), pattern = 그때 보스가 하던 패턴(빈칸이면 추적·대기 중).
        private void OnBossShoved(float distance)
        {
            if (_summary == null || _finished) return;
            WriteEvent("boss_shoved", distance, 0, _bossController.gameObject, CurrentBossPattern(), CurrentBossPatternId(),
                _bossController.CurrentStrikeId);
        }

        private void OnPlayerDied() => Finish("defeat");

        private void OnBossDied() => Finish("victory");

        private void Finish(string outcome)
        {
            if (_finished || _summary == null) return;
            _finished = true;

            _summary.outcome = outcome;
            _summary.durationSeconds = Time.timeSinceLevelLoad - _startedAt;
            _summary.calibration = _bossController != null && _bossController.IsCalibrationAttempt;
            _summary.playerHealthRatio = _player != null ? Ratio(_player.CurrentHealth, _player.MaxHealth) : -1f;
            _summary.bossHealthRatio = _boss != null ? Ratio(_boss.CurrentHealth, _boss.MaxHealth) : -1f;
            if (outcome == "victory" || outcome == "defeat")
            {
                _summary.score = _score.Finish(outcome == "victory", Time.time);
                _summary.scoreFightSeconds = _score.FightSeconds(Time.time);
                _summary.scoreStrikesAvoided = _score.StrikesAvoided;
                _summary.hasScore = true;
            }

            WriteEvent("attempt_end", 0f, 0, null);
            AppendAttemptRow();
            Debug.Log(
                $"{LogPrefix} attempt {_summary.attempt} {outcome} · {_summary.durationSeconds:F1}s · " +
                $"player hits {_summary.playerHitsTaken}, boss hits {_summary.bossHitsTaken}, " +
                $"boss HP {_summary.bossHealthRatio:P0}",
                this);
        }

        private static float Ratio(float value, float max) => max > 0f ? Mathf.Clamp01(value / max) : 0f;

        private static void EnsureSession()
        {
            if (s_sessionId != null) return;

            s_sessionId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            s_participantId = ReadParticipantId();
            // 실행 인자 -telemetryRoot <폴더>로 기록 위치를 바꿀 수 있다(학습 실행 파일은 저장소 밖 폴더에 모은다).
            string root = ReadArg("-telemetryRoot") ?? Path.Combine(Application.persistentDataPath, "Telemetry");
            s_directory = Path.Combine(root, s_sessionId);
            Debug.Log($"{LogPrefix} session {s_sessionId} · participant {s_participantId} · {s_directory}");
        }

        private static string ReadArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name && !string.IsNullOrWhiteSpace(args[i + 1])) return args[i + 1].Trim();
            }

            return null;
        }

        private static string ReadParticipantId()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-participant" && !string.IsNullOrWhiteSpace(args[i + 1])) return args[i + 1].Trim();
            }

            return "anon";
        }

        private void WriteEvent(string type, float amount, int attackIndex, GameObject source, string pattern = "",
            int patternId = 0, int strikeId = 0, string outcome = "", string move = "",
            float scoreWeight = 0f, float scorePressure = 0f, float scorePoints = 0f, float distance = 0f)
        {
            var e = new TelemetryEvent
            {
                schemaVersion = SchemaVersion,
                sessionId = s_sessionId,
                attempt = _summary.attempt,
                type = type,
                time = Time.timeSinceLevelLoad,
                amount = amount,
                attackIndex = attackIndex,
                pattern = pattern,
                patternId = patternId,
                strikeId = strikeId,
                outcome = outcome,
                move = move,
                scoreWeight = scoreWeight,
                scorePressure = scorePressure,
                scorePoints = scorePoints,
                distance = distance,
                bossPhase = _bossController != null ? _bossController.CurrentPhase : 0,
                bossSegment = _bossController != null ? _bossController.CurrentSegment : 0,
                source = source != null ? source.name : "",
                playerHealth = _player != null ? _player.CurrentHealth : -1f,
                bossHealth = _boss != null ? _boss.CurrentHealth : -1f,
            };

            Append("events.jsonl", JsonUtility.ToJson(e) + "\n", null);
        }

        private void AppendAttemptRow()
        {
            AttemptSummary s = _summary;
            string row = string.Join(",",
                s.schemaVersion.ToString(CultureInfo.InvariantCulture),
                Csv(s.buildVersion), Csv(s.sessionId), Csv(s.participantId),
                s.attempt.ToString(CultureInfo.InvariantCulture),
                Csv(s.scene), Csv(s.condition), Csv(s.difficulty), Csv(s.startedUtc), Csv(s.outcome),
                F(s.durationSeconds),
                s.playerHitsTaken.ToString(CultureInfo.InvariantCulture), F(s.playerDamageTaken), F(s.playerHealthRatio),
                s.bossHitsTaken.ToString(CultureInfo.InvariantCulture), F(s.bossDamageTaken), F(s.bossHealthRatio),
                F(s.playerMaxHealth), F(s.bossMaxHealth),
                s.playerEvades.ToString(CultureInfo.InvariantCulture),
                s.playerParries.ToString(CultureInfo.InvariantCulture),
                s.bossPatterns.ToString(CultureInfo.InvariantCulture),
                s.tuning != null ? F(s.tuning.patternInterval) : "",
                s.tuning != null ? F(s.tuning.overheadWeight) : "",
                s.tuning != null ? F(s.tuning.comboWeight) : "",
                s.tuning != null ? F(s.tuning.sweepWeight) : "",
                s.tuning != null ? F(s.tuning.thirdStrikeProbability) : "",
                s.tuning != null ? F(s.tuning.thirdStrikePause) : "",
                s.tuning != null ? F(s.tuning.dashCooldown) : "",
                s.tuning != null ? F(s.tuning.leapCooldown) : "",
                s.tuning != null ? F(s.tuning.mainPatternCooldown) : "",
                s.calibration ? "1" : "0",
                s.bossPhaseReached.ToString(CultureInfo.InvariantCulture),
                s.bossSegmentReached.ToString(CultureInfo.InvariantCulture),
                s.bossStrikes.ToString(CultureInfo.InvariantCulture),
                s.bossStrikesHit.ToString(CultureInfo.InvariantCulture),
                s.bossStrikesEvaded.ToString(CultureInfo.InvariantCulture),
                s.bossStrikesParried.ToString(CultureInfo.InvariantCulture),
                s.bossStrikesMissed.ToString(CultureInfo.InvariantCulture),
                s.playerAttacks.ToString(CultureInfo.InvariantCulture),
                s.playerMovesUsed.ToString(CultureInfo.InvariantCulture),
                s.playerCommandHits.ToString(CultureInfo.InvariantCulture),
                s.hasScore ? I(s.score.Total) : "", s.hasScore ? s.score.Rank : "",
                s.hasScore ? I(s.score.BossStrength) : "", s.hasScore ? I(s.score.Skill) : "",
                s.hasScore ? I(s.score.Time) : "", s.hasScore ? I(s.score.Clear) : "",
                s.hasScore ? F(s.scoreFightSeconds) : "", s.hasScore ? I(s.scoreStrikesAvoided) : "",
                s.hasScore ? ScorePatternWeights.Version : "") + "\n";

            Append("attempts.csv", row, AttemptSummary.CsvHeader + "\n");
        }

        private void Append(string fileName, string text, string headerIfNew)
        {
            if (!writeFiles) return;

            try
            {
                Directory.CreateDirectory(s_directory);
                string path = Path.Combine(s_directory, fileName);
                if (headerIfNew != null && !File.Exists(path)) File.AppendAllText(path, headerIfNew, Encoding.UTF8);
                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                // 기록 실패가 게임을 멈추면 안 된다.
                Debug.LogWarning($"{LogPrefix} write failed: {exception.Message}", this);
            }
        }

        private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Csv(string value)
        {
            value ??= "";
            return value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        [Serializable]
        private sealed class TelemetryEvent
        {
            public int schemaVersion;
            public string sessionId;
            public int attempt;
            public string type;
            public float time;
            public float amount;
            public int attackIndex;
            public string pattern;
            public int patternId;
            public int strikeId;
            public string outcome;
            // 플레이어 공격 이름(player_attack·boss_hit). 그 밖의 이벤트는 빈칸.
            public string move;
            // 점수(스키마 5): boss_strike만 채운다. 가중치·압박 배율과 그 타격으로 얻은 보스 세기 점수(맞았으면 0).
            public float scoreWeight;
            public float scorePressure;
            public float scorePoints;
            public float distance;
            public int bossPhase;
            public int bossSegment;
            public string source;
            public float playerHealth;
            public float bossHealth;
        }

        private sealed class AttemptSummary
        {
            public const string CsvHeader =
                "schema_version,build_version,session_id,participant_id,attempt,scene,condition,difficulty," +
                "started_utc,outcome,duration_s,player_hits_taken,player_damage_taken,player_health_ratio," +
                "boss_hits_taken,boss_damage_taken,boss_health_ratio,player_max_health,boss_max_health," +
                "player_evades,player_parries,boss_patterns,tuning_pattern_interval," +
                "tuning_overhead_weight,tuning_combo_weight,tuning_sweep_weight," +
                "tuning_third_strike_probability,tuning_third_strike_pause,tuning_dash_cooldown," +
                "tuning_leap_cooldown,tuning_main_pattern_cooldown,calibration,boss_phase_reached,boss_segment_reached," +
                "boss_strikes,boss_strikes_hit,boss_strikes_evaded,boss_strikes_parried,boss_strikes_missed," +
                "player_attacks,player_moves_used,player_command_hits," +
                "score_total,score_rank,score_boss_strength,score_skill,score_time,score_clear,score_fight_s," +
                "score_strikes_avoided,score_weights_version";

            public int schemaVersion;
            public string buildVersion;
            public string sessionId;
            public string participantId;
            public int attempt;
            public string scene;
            public string condition;
            public string difficulty;
            public string startedUtc;
            public string outcome;
            public float durationSeconds;
            public int playerHitsTaken;
            public float playerDamageTaken;
            public float playerHealthRatio;
            public int bossHitsTaken;
            public float bossDamageTaken;
            public float bossHealthRatio;
            public float playerMaxHealth;
            public float bossMaxHealth;
            public int playerEvades;
            public int playerParries;
            public int bossPatterns;
            public BossTuning tuning;
            public bool calibration;
            // 도달한 가장 깊은 페이즈(1·2)와 구간(phase*10+segment, 예: 23 = 2페이즈 3구간).
            public int bossPhaseReached = 1;
            public int bossSegmentReached = 11;
            public int bossStrikes;
            public int bossStrikesHit;
            public int bossStrikesEvaded;
            public int bossStrikesParried;
            public int bossStrikesMissed;
            public int playerAttacks;
            public int playerMovesUsed;
            public int playerCommandHits;
            public bool hasScore;
            public CombatScore.Result score;
            public float scoreFightSeconds;
            public int scoreStrikesAvoided;
        }
    }
}
