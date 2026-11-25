using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectSoullike
{
    // 게임에서 학습된 보스 디렉터(ONNX)를 켠다. 수호자 보스가 있는 씬이 열릴 때 하나 만든다.
    // 모델(Resources/ML/BossDirector)이 없거나 설정에서 끄면 아무것도 하지 않고, 보스는 기본 비중 추첨을 쓴다.
    // 추론 중 오류가 나면 디렉터가 스스로 물러나 기본 추첨으로 돌아간다(BossDirectorAgent.Choose).
    [DisallowMultipleComponent]
    public sealed class BossDirectorRuntime : MonoBehaviour
    {
        public const string ModelResource = "ML/BossDirector";
        private const string EnabledKey = "soullike.director.enabled";

        private BossDirectorAgent _agent;

        // 디렉터 사용 여부(기본 켬). 비교 실험의 조건 A(고정)는 끈다.
        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(EnabledKey, 1) == 1;
            set { PlayerPrefs.SetInt(EnabledKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        public static bool IsActive { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallOnFirstScene()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureInstance();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => EnsureInstance();

        private static void EnsureInstance()
        {
            if (SimulationMode.Active || !Enabled) return;
            if (FindAnyObjectByType<BossDirectorRuntime>() != null) return;
            BossController boss = FindAnyObjectByType<BossController>();
            if (boss == null || !boss.UseGuardianPatterns || FindAnyObjectByType<PlayerHealth>() == null) return;
            var model = Resources.Load<Unity.InferenceEngine.ModelAsset>(ModelResource);
            if (model == null) return;
            new GameObject(nameof(BossDirectorRuntime)).AddComponent<BossDirectorRuntime>().Begin(model, boss);
        }

        private void Begin(Unity.InferenceEngine.ModelAsset model, BossController boss)
        {
            try
            {
                _agent = BossDirectorAgent.Create(transform, model, inferenceOnly: true);
                _agent.BeginFight(boss, FindAnyObjectByType<PlayerHealth>());
                IsActive = true;
                CombatTelemetryRecorder.ConditionOverride = "rl";
                Debug.Log("[Boss Director] inference on", this);
            }
            catch (System.Exception error)
            {
                Debug.LogWarning("[Boss Director] could not start, using default patterns: " + error.Message, this);
                if (ReferenceEquals(BossController.PatternPolicy, _agent)) BossController.PatternPolicy = null;
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            IsActive = false;
            if (CombatTelemetryRecorder.ConditionOverride == "rl") CombatTelemetryRecorder.ConditionOverride = null;
            if (_agent != null) _agent.EndFight(0, 1f, 1f, false);
        }
    }
}
