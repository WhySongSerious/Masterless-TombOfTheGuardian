using System;
using UnityEngine;

namespace ProjectSoullike
{
    // Parry 입력(키보드 Left Ctrl, 패드 L1) 직후 0.25초 창 안에 패링 가능한 공격이 닿으면
    // 피해 없이 막고 보스를 경직시킨다. 1차 QA(11/11)에서 너무 쉽다는 의견으로 0.4초에서 줄였다. 시도마다 기력 12를 쓴다.
    // 소리·불꽃·히트스톱은 Parried 이벤트로 SwordFeedback·CombatHitFeel이 낸다.
    // 모션: 막는 자세 Parry1H01_R - Loop, 성공 Parry1H01_R - Hit (Tools > Project Soullike > Set Up Guardian Dash & Parry Animations).
    // Animator에 해당 상태가 없으면 화면 문구로만 보여 준다. KeyboardPlayerController가 없으면 자동으로 붙인다.
    // 모션 다듬기: 막는 자세로 부드럽게 들어가고(animationBlend), 성공하면 공격자 쪽으로 몸을 돌려 살짝 밀려나며
    // 성공 모션을 거의 끝까지 보여 준 뒤 천천히 기본 자세로 돌아온다(exitBlend). 성공 직후부터는 반격·공격·구르기로 끊을 수 있다.
    [DisallowMultipleComponent]
    public sealed class PlayerParry : MonoBehaviour, IParryReceiver
    {
        public const string LoopStatePath = "Base Layer.Parry Loop";
        public const string HitStatePath = "Base Layer.Parry Hit";
        private static readonly int LoopStateHash = Animator.StringToHash(LoopStatePath);
        private static readonly int HitStateHash = Animator.StringToHash(HitStatePath);
        private static readonly int LocomotionStateHash = Animator.StringToHash("Base Layer.Locomotion");

        [SerializeField, Min(0.05f)] private float parryDuration = 0.55f;
        [SerializeField, Min(0f)] private float windowOpen = 0f;
        [SerializeField, Min(0f)] private float windowClose = 0.25f;
        [SerializeField, Min(0f)] private float staminaCost = 12f;
        [Tooltip("성공 모션을 보여 주는 동안 더 멈춰 있는 시간(초)")]
        [SerializeField, Min(0f)] private float successHold = 0.6f;
        [Tooltip("성공 후 이 시간(초)이 지나면 공격 입력으로 끊을 수 있다(반격·구르기는 바로 가능)")]
        [SerializeField, Min(0f)] private float successCancelAfter = 0.15f;
        [Tooltip("막는 자세로 들어갈 때 블렌드(초)")]
        [SerializeField, Min(0f)] private float animationBlend = 0.1f;
        [Tooltip("성공 모션으로 넘어갈 때 블렌드(초)")]
        [SerializeField, Min(0f)] private float hitBlend = 0.04f;
        [Tooltip("패링이 끝나 기본 자세로 돌아갈 때 블렌드(초)")]
        [SerializeField, Min(0f)] private float exitBlend = 0.22f;
        [Tooltip("성공 시 충격으로 뒤로 밀려나는 거리(m)와 시간(초)")]
        [SerializeField, Min(0f)] private float successPushback = 0.3f;
        [SerializeField, Min(0.01f)] private float successPushbackTime = 0.15f;

        [Header("Temporary Feedback")]
        [SerializeField] private bool showOverlay = true;
        [Tooltip("성공 문구(PARRY!)를 보여 주는 실제 시간(초)")]
        [SerializeField, Min(0f)] private float resultDisplayDuration = 0.6f;

        private PlayerStamina _stamina;
        private PlayerHealth _health;
        private Animator _animator;
        private CharacterController _characterController;
        private float _elapsed;
        private float _successTime;
        private float _pushbackRemaining;
        private Vector3 _pushbackDirection;
        private float _endTime;
        private bool _succeeded;
        private bool _playedAnimation;
        private float _resultRemaining;
        private GUIStyle _style;

        public event Action<DamageInfo> Parried;
        public event Action<string> Failed;
        public bool IsParrying { get; private set; }
        public bool IsWindowOpen => IsParrying && !_succeeded && _elapsed >= windowOpen && _elapsed <= windowClose;
        public bool Succeeded => IsParrying && _succeeded;
        // 성공 모션을 공격 입력으로 끊어도 되는지.
        public bool CanCancelIntoAttack => Succeeded && _elapsed - _successTime >= successCancelAfter;

        private void Awake()
        {
            // OnGUI는 GUILayout을 쓰지 않으므로 레이아웃 이벤트를 건너뛴다(멘토링 P2 프로파일: OnGUI가 프레임마다 두 번 불리던 비용).
            useGUILayout = false;
            _stamina = GetComponent<PlayerStamina>();
            _health = GetComponent<PlayerHealth>();
            _animator = GetComponentInChildren<Animator>(includeInactive: true);
            _characterController = GetComponent<CharacterController>();
        }

        private void OnDisable()
        {
            CancelParry();
        }

        public bool TryStartParry()
        {
            if (IsParrying || (_health != null && !_health.IsAlive)) return false;
            // KeyboardPlayerController가 Awake에서 PlayerStamina를 붙이므로, 이 Awake가 먼저 돌면 비어 있다.
            // 그래서 지금까지 패링이 기력을 쓰지 않았다(1차 QA 3번). 쓸 때 다시 찾는다.
            if (_stamina == null) _stamina = GetComponent<PlayerStamina>();
            if (_stamina != null && !_stamina.TryConsume(staminaCost)) return false;
            IsParrying = true;
            _succeeded = false;
            _elapsed = 0f;
            _endTime = parryDuration;
            _pushbackRemaining = 0f;
            _playedAnimation = CrossFade(LoopStateHash, animationBlend);
            return true;
        }

        // 구르기·피격 등으로 끊길 때. 성공한 뒤 반격·공격으로 넘어갈 때는 실패로 세지 않는다.
        // 다음 동작이 자기 모션을 재생하므로 여기서는 Animator를 건드리지 않는다.
        // reason은 실패 기록(텔레메트리 player_parry_failed_*)에 남는다. 막는 자세에서 맞아 끊기면 "hit".
        public void CancelParry() => CancelParry("cancelled");

        public void CancelParry(string reason)
        {
            if (IsParrying && !_succeeded) Failed?.Invoke(reason);
            IsParrying = false;
            _pushbackRemaining = 0f;
        }

        public bool TryParry(DamageInfo incoming)
        {
            if (!IsWindowOpen || !isActiveAndEnabled || (_health != null && (!_health.IsAlive || _health.IsInvulnerable))) return false;
            _succeeded = true;
            _successTime = _elapsed;
            _resultRemaining = resultDisplayDuration;
            _endTime = Mathf.Max(_endTime, _elapsed + successHold);
            FaceAttacker(incoming);
            _playedAnimation |= CrossFade(HitStateHash, hitBlend);
            Parried?.Invoke(incoming);
            Debug.Log($"[Player Parry] Parried {(incoming.Source != null ? incoming.Source.name : "?")} at {_elapsed:F2}s", this);
            return true;
        }

        private void Update()
        {
            // 패링 슬로모션(게임 시간 약 0.12배) 동안에도 정해진 실제 시간만 보이게 한다. 게임 시간으로 줄이면 2초 가까이 남아 반격 MISS와 겹쳤다.
            if (_resultRemaining > 0f) _resultRemaining -= Time.unscaledDeltaTime;
            if (!IsParrying) return;
            _elapsed += Time.deltaTime;
            UpdatePushback();
            if (_elapsed < _endTime) return;
            if (!_succeeded) Failed?.Invoke("window_expired");
            IsParrying = false;
            if (_playedAnimation) CrossFade(LocomotionStateHash, exitBlend);
        }

        // 공격자 쪽으로 몸을 돌리고, 그 반대쪽으로 밀려날 준비를 한다.
        private void FaceAttacker(DamageInfo incoming)
        {
            Vector3 toAttacker = incoming.Source != null
                ? incoming.Source.transform.position - transform.position
                : -incoming.Direction;
            toAttacker.y = 0f;
            if (toAttacker.sqrMagnitude < 0.0001f) return;
            toAttacker.Normalize();
            transform.rotation = Quaternion.LookRotation(toAttacker, Vector3.up);
            _pushbackDirection = -toAttacker;
            _pushbackRemaining = successPushback;
        }

        private void UpdatePushback()
        {
            if (_pushbackRemaining <= 0f || _characterController == null || !_characterController.enabled) return;
            float step = Mathf.Min(_pushbackRemaining, successPushback / successPushbackTime * Time.deltaTime);
            _pushbackRemaining -= step;
            _characterController.Move(_pushbackDirection * step);
        }

        private bool CrossFade(int stateHash, float blend)
        {
            if (_animator == null || _animator.runtimeAnimatorController == null || !_animator.HasState(0, stateHash))
                return false;
            _animator.CrossFadeInFixedTime(stateHash, blend, 0, 0f);
            return true;
        }

        private void OnGUI()
        {
            if (!showOverlay || (!IsParrying && _resultRemaining <= 0f)) return;
            _style ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, font = UiTheme.Font(UiTheme.Face.Label) };
            float uiScale = Mathf.Clamp(Screen.height / 1080f, 0.7f, 1.4f);
            _style.fontSize = Mathf.RoundToInt(28f * uiScale);
            bool success = _resultRemaining > 0f;
            _style.normal.textColor = success ? new Color(1f, 0.85f, 0.3f) : new Color(0.7f, 0.85f, 1f);
            GUI.Label(new Rect(0f, Screen.height * 0.62f, Screen.width, 48f * uiScale),
                success ? "PARRY!" : IsWindowOpen ? "PARRY" : "...", _style);
        }

        private void OnValidate()
        {
            windowOpen = Mathf.Clamp(windowOpen, 0f, parryDuration);
            windowClose = Mathf.Clamp(windowClose, windowOpen, parryDuration);
        }
    }
}
