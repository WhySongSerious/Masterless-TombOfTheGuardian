using System;
using System.Collections;
using System.Globalization;
using UnityEngine;

namespace ProjectSoullike
{
    // 보스 디렉터 학습·자동 평가 결투장. 씬을 다시 부르지 않고 판마다 플레이어·보스를 새로 만들어 리셋한다.
    // 상대는 규칙 봇(초보·중급·숙련, 판마다 고르게 섞음). 판이 끝나면 디렉터에 결말 보상을 주고 다음 판을 연다.
    // 실행 인자:
    //   -condition rl|fixed|rule rl = PPO 디렉터(C, 기본), fixed = 디렉터 없이 기본 추첨(A), rule = 규칙 디렉터(B)
    //   -botSkill all|novice|intermediate|expert   (기본 all)
    //   -episodes N              N판 뒤 종료(평가용, 0이면 계속)
    //   -seed N                  봇 난수 시드 시작값
    //   -telemetryRoot <폴더>    전투 로그 위치(CombatTelemetryRecorder)
    //   -dodgeLead/-parryLead 초, -defenseFailScale 배수   봇 방어 타이밍·실패율 보정(봇을 사람 기록에 맞출 때)
    // 화면·소리는 쓰지 않는다(-batchmode -nographics로 실행). 시간 배율은 학습기(time_scale)가 정한다.
    public sealed class BossDirectorArena : MonoBehaviour
    {
        [SerializeField] private GameObject playerPrefab;
        [SerializeField] private GameObject bossPrefab;
        [SerializeField] private float startDistance = 9f;
        [Tooltip("한 판 최대 게임 시간(초). 넘기면 시간 초과로 끝낸다")]
        [SerializeField] private float episodeTimeLimit = 240f;
        [SerializeField] private Unity.InferenceEngine.ModelAsset model;

        private BossDirectorAgent _agent;
        private GameObject _playerObject, _bossObject;
        private PlayerHealth _player;
        private GolemHealth _boss;
        private CombatTelemetryRecorder _recorder;
        private float _fightStart;
        private bool _fightOver;
        private int _episode;
        private int _seed = 1000;
        private int _episodeLimit;
        private string _condition = "rl";
        private RuleBotSkill? _fixedSkill;
        // 실력 단계별 플레이어 체력 배수(초보·중급·숙련). 규칙 봇의 방어가 사람보다 서툴러 생기는 차이를 메워
        // 숙련 봇이 가끔 이기게 맞춘다. 실행 인자 -hpScale 1.5,2.8,4.5 로 바꾼다. 디렉터의 관측·보상은 체력 비율이라 영향이 없다.
        // 11/17 보정(고정 프레임 1/60초, 고정 보스, 40판씩): 초보 승률 0%·끝 보스 체력 0.69, 중급 5%·0.25, 숙련 55%·0.06.
        private float[] _hpScale = { 1.5f, 2.8f, 4.5f };

        public int Episode => _episode;

        private void Awake()
        {
            SimulationMode.Active = true;
            AudioListener.volume = 0f;
            Application.runInBackground = true;
            // 학습기 없이 돌릴 때(평가)도 학습과 같게: 한 프레임 = 게임 1/60초로 고정하고 최대한 빨리 돌린다.
            Time.captureFramerate = 60;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            ReadArguments();
        }

        private void Start()
        {
            if (_condition == "rl") _agent = BossDirectorAgent.Create(transform, model, inferenceOnly: model != null && !Unity.MLAgents.Academy.Instance.IsCommunicatorOn);
            StartCoroutine(Loop());
        }

        private void ReadArguments()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                string value = args[i + 1].Trim().ToLowerInvariant();
                switch (args[i])
                {
                    case "-condition": _condition = value; break;
                    case "-episodes": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _episodeLimit); break;
                    case "-seed": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _seed); break;
                    case "-hpScale":
                        string[] parts = value.Split(',');
                        for (int k = 0; k < parts.Length && k < _hpScale.Length; k++) _hpScale[k] = ParseFloat(parts[k], _hpScale[k]);
                        break;
                    case "-patternShareCap": BossDirectorAgent.PatternShareCap = ParseFloat(value, 0f); break;
                    case "-patternSharePenalty": BossDirectorAgent.PatternSharePenalty = ParseFloat(value, 0.05f); break;
                    case "-dodgeLead": RuleBot.DodgeLeadOverride = ParseFloat(value, -1f); break;
                    case "-parryLead": RuleBot.ParryLeadOverride = ParseFloat(value, -1f); break;
                    case "-defenseFailScale": RuleBot.DefenseFailScale = ParseFloat(value, 1f); break;
                    case "-botSkill":
                        if (value == "novice") _fixedSkill = RuleBotSkill.Novice;
                        else if (value == "intermediate") _fixedSkill = RuleBotSkill.Intermediate;
                        else if (value == "expert") _fixedSkill = RuleBotSkill.Expert;
                        break;
                }
            }
        }

        private static float ParseFloat(string value, float fallback) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : fallback;

        private IEnumerator Loop()
        {
            while (_episodeLimit <= 0 || _episode < _episodeLimit)
            {
                yield return null; // 지난 판 오브젝트가 지워진 다음 프레임에 새로 만든다
                SpawnFight();
                while (!_fightOver)
                {
                    if (Time.time - _fightStart > episodeTimeLimit) FinishFight(0, timedOut: true);
                    yield return null;
                }
                yield return null;
                Cleanup();
            }
            Debug.Log($"[Boss Director Arena] {_episode} episodes done");
            Application.Quit();
        }

        private void SpawnFight()
        {
            _episode++;
            RuleBotSkill skill = _fixedSkill ?? (RuleBotSkill)(_episode % 3);
            _fightOver = false;

            _playerObject = Instantiate(playerPrefab, new Vector3(0f, 0f, -startDistance * 0.5f), Quaternion.identity);
            _bossObject = Instantiate(bossPrefab, new Vector3(0f, 0f, startDistance * 0.5f), Quaternion.Euler(0f, 180f, 0f));
            foreach (Animator animator in _playerObject.GetComponentsInChildren<Animator>(true)) animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (Animator animator in _bossObject.GetComponentsInChildren<Animator>(true)) animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            _player = _playerObject.GetComponent<PlayerHealth>();
            _player.SetMaxHealthForSimulation(_player.MaxHealth * _hpScale[(int)skill]);
            _boss = _bossObject.GetComponent<GolemHealth>();
            var bossController = _bossObject.GetComponent<BossController>();
            var controller = _playerObject.GetComponent<KeyboardPlayerController>();
            bossController.SetTarget(_playerObject.transform);
            var bot = new RuleBot(controller, bossController, skill, _seed + _episode);
            controller.CommandSource = bot;

            _player.Died += OnPlayerDied;
            _boss.Died += OnBossDied;

            CombatTelemetryRecorder.ParticipantOverride = bot.ParticipantId;
            CombatTelemetryRecorder.ConditionOverride = _condition;
            _recorder = new GameObject(nameof(CombatTelemetryRecorder)).AddComponent<CombatTelemetryRecorder>();

            _fightStart = Time.time;
            if (_agent != null) _agent.BeginFight(bossController, _player);
            else if (_condition == "rule") BossController.PatternPolicy = new RuleDirector(bossController);
            else BossController.PatternPolicy = null;
        }

        private void OnPlayerDied() => FinishFight(-1, timedOut: false);
        private void OnBossDied() => FinishFight(1, timedOut: false);

        private void FinishFight(int outcome, bool timedOut)
        {
            if (_fightOver) return;
            _fightOver = true;
            float playerRatio = _player != null && _player.MaxHealth > 0f ? _player.CurrentHealth / _player.MaxHealth : 0f;
            float bossRatio = _boss != null && _boss.MaxHealth > 0f ? _boss.CurrentHealth / _boss.MaxHealth : 0f;
            if (_agent != null) _agent.EndFight(outcome, playerRatio, bossRatio, timedOut);
            Debug.Log($"[Boss Director Arena] episode {_episode} {(timedOut ? "timeout" : outcome > 0 ? "player_win" : "boss_win")} " +
                $"player {playerRatio:P0} boss {bossRatio:P0} {Time.time - _fightStart:F0}s");
        }

        private void Cleanup()
        {
            if (_player != null) _player.Died -= OnPlayerDied;
            if (_boss != null) _boss.Died -= OnBossDied;
            if (_recorder != null) Destroy(_recorder.gameObject); // 결과가 없으면 aborted로 남는다
            if (_playerObject != null) Destroy(_playerObject);
            if (_bossObject != null) Destroy(_bossObject);
            _recorder = null; _player = null; _boss = null; _playerObject = null; _bossObject = null;
        }
    }
}
