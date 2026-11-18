using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ProjectSoullike
{
    // 타격감: 맞는 순간 아주 짧게 시간을 멈추고(히트스톱) 카메라를 흔든다. 플레이어에 붙인다.
    // - 내가 때림: 공격 피해에 비례해 약공은 짧게, 강공·피니시는 조금 길게.
    // - 내가 맞음: 보스 공격 피해에 비례해 더 묵직하게.
    // - 패링 성공: 가장 또렷하게 끊는다.
    // - 패링 반격 적중: 강공보다 조금 더 길게 멈추고 크게 흔든다.
    // 값은 모두 짧게 둔다. 길게 멈추거나 크게 흔들면 다음 보스 전조(#8 가독성)를 놓친다.
    [DisallowMultipleComponent]
    public sealed class CombatHitFeel : MonoBehaviour
    {
        [System.Serializable]
        public struct Feel
        {
            [Tooltip("멈춤 시간(실제 초)")]
            [Min(0f)] public float hitStop;
            [Tooltip("멈춤 동안의 시간 배율. 0이면 완전히 멈춘다")]
            [Range(0f, 1f)] public float hitStopTimeScale;
            [Tooltip("카메라 흔들림 크기(m)와 시간(초)")]
            [Min(0f)] public float shake;
            [Min(0f)] public float shakeDuration;
        }

        [Header("내가 때림 (피해 20 ~ 60 사이를 보간)")]
        [SerializeField] private Feel lightHit = new Feel
            { hitStop = 0.065f, hitStopTimeScale = 0.04f, shake = 0.05f, shakeDuration = 0.13f };
        [SerializeField] private Feel heavyHit = new Feel
            { hitStop = 0.14f, hitStopTimeScale = 0.02f, shake = 0.12f, shakeDuration = 0.24f };
        [SerializeField, Min(0f)] private float lightDamage = 20f;
        [SerializeField, Min(0f)] private float heavyDamage = 60f;
        [Tooltip("강공 계열(△/R2·우클릭)은 피해가 작아도 최소 이 비율만큼 강공 느낌을 준다(1차 QA 2번 타격감)")]
        [SerializeField, Range(0f, 1f)] private float heavyInputMinimumBlend = 0.7f;

        [Header("내가 맞음 (피해 12 ~ 32 사이를 보간)")]
        [SerializeField] private Feel lightDamaged = new Feel
            { hitStop = 0.05f, hitStopTimeScale = 0.1f, shake = 0.07f, shakeDuration = 0.18f };
        [SerializeField] private Feel heavyDamaged = new Feel
            { hitStop = 0.09f, hitStopTimeScale = 0.05f, shake = 0.13f, shakeDuration = 0.28f };
        [SerializeField, Min(0f)] private float lightIncoming = 12f;
        [SerializeField, Min(0f)] private float heavyIncoming = 32f;

        [Header("패링 성공")]
        [SerializeField] private Feel parry = new Feel
            { hitStop = 0.12f, hitStopTimeScale = 0f, shake = 0.06f, shakeDuration = 0.15f };

        [Header("패링 반격 적중")]
        [SerializeField] private Feel riposteHit = new Feel
            { hitStop = 0.16f, hitStopTimeScale = 0.02f, shake = 0.12f, shakeDuration = 0.25f };

        // ---------- 멘토링 P1: 화면·카메라·몸 반응 ----------
        // 히트스톱·흔들림만으로는 "맞았다/때렸다"가 약했다. 아래는 모두 0.1~0.45초 안에 사라져 다음 전조를 가리지 않는다.
        [Header("카메라 킥 (방향 있는 밀림, m)")]
        [Tooltip("때림: 맞힌 쪽으로 파고드는 거리. 약공(x) ~ 강공(y)")]
        [SerializeField] private Vector2 hitKick = new Vector2(0.06f, 0.16f);
        [Tooltip("맞음: 맞은 방향으로 밀리는 거리. 약한 피해(x) ~ 강한 피해(y)")]
        [SerializeField] private Vector2 hurtKick = new Vector2(0.12f, 0.28f);
        [Tooltip("패링 성공: 앞으로 찍히는 거리")]
        [SerializeField, Min(0f)] private float parryKick = 0.3f;
        [SerializeField, Min(0.01f)] private float kickDuration = 0.18f;

        [Header("화면 연출 (포스트프로세스)")]
        [Tooltip("패링 성공 섬광: 노출을 이 값까지 순간 올린다(기본 장면 0.45)")]
        [SerializeField] private float parryFlashExposure = 1.7f;
        [SerializeField, Min(0.01f)] private float parryFlashDuration = 0.22f;
        [Tooltip("패링·반격 적중 때 색수차 세기")]
        [SerializeField, Range(0f, 1f)] private float parryChromatic = 0.75f;
        [Tooltip("맞음: 화면 가장자리 붉은 테")]
        [SerializeField] private Color hurtVignetteColor = new Color(0.55f, 0.02f, 0.02f);
        [SerializeField, Range(0f, 1f)] private float hurtVignetteIntensity = 0.42f;
        [SerializeField, Range(0f, 1f)] private float hurtChromatic = 0.35f;
        [SerializeField, Min(0.01f)] private float hurtDuration = 0.45f;
        [Tooltip("강공·피니시 적중 때 색수차(약공은 0)")]
        [SerializeField, Range(0f, 1f)] private float heavyHitChromatic = 0.3f;

        [Header("보스 몸 반응 (적중 순간 몸이 맞은 방향으로 흔들림)")]
        [Tooltip("밀리는 거리(m). 약공(x) ~ 강공(y)")]
        [SerializeField] private Vector2 bossFlinchDistance = new Vector2(0.05f, 0.12f);
        [Tooltip("뒤로 젖혀지는 각도(도). 약공(x) ~ 강공(y)")]
        [SerializeField] private Vector2 bossFlinchTilt = new Vector2(3f, 9f);
        [SerializeField, Min(0.01f)] private float bossFlinchDuration = 0.16f;

        private KeyboardPlayerController _player;
        private PlayerHealth _health;
        private PlayerParry _parry;
        private float _stopUntil;
        private bool _stopping;

        private Volume _flashVolume, _hurtVolume;
        private VolumeProfile _flashProfile, _hurtProfile;
        private ChromaticAberration _flashChromatic;
        private float _flash, _flashLength = 1f, _hurt, _hurtLength = 1f;

        private BossController _boss;
        private Transform _bossBody;
        private Vector3 _bossBodyRestPosition;
        private Quaternion _bossBodyRestRotation;
        private Vector3 _flinchDirection;
        private float _flinchDistance, _flinchTilt, _flinchRemaining;

        // 히트스톱 중이면 다른 곳(반격 슬로모션)이 시간 배율을 덮어쓰지 않는다.
        public bool IsHitStopping => _stopping;

        private void Awake()
        {
            _player = GetComponent<KeyboardPlayerController>();
            _health = GetComponent<PlayerHealth>();
            // PlayerParry는 KeyboardPlayerController가 Awake에서 붙이기도 해서, Awake 순서와 상관없이 같은 것을 쓰게 여기서도 보장한다.
            _parry = GetComponent<PlayerParry>();
            if (_parry == null && _player != null) _parry = gameObject.AddComponent<PlayerParry>();
            // 패드 진동도 같은 순간(맞힘·맞음·패링)에 나므로 함께 붙인다. 이미 프리팹에 있으면 그것을 쓴다.
            if (GetComponent<CombatHaptics>() == null) gameObject.AddComponent<CombatHaptics>();
        }

        private void OnEnable()
        {
            if (_player != null) _player.AttackHit += OnAttackHit;
            if (_health != null) _health.DamageTaken += OnDamageTaken;
            if (_parry != null) _parry.Parried += OnParried;
        }

        private void OnDisable()
        {
            if (_player != null) _player.AttackHit -= OnAttackHit;
            if (_health != null) _health.DamageTaken -= OnDamageTaken;
            if (_parry != null) _parry.Parried -= OnParried;
            EndStop();
            _flash = _hurt = 0f;
            ApplyScreen();
            EndFlinch();
        }

        private void Start()
        {
            // 장면 기본 Volume 위에 섞는 전용 Volume 두 개. 무게 0이면 아무 영향이 없다.
            _flashVolume = CreateVolume("HitFeel Flash", out _flashProfile);
            ColorAdjustments exposure = _flashProfile.Add<ColorAdjustments>(true);
            exposure.postExposure.Override(parryFlashExposure);
            _flashChromatic = _flashProfile.Add<ChromaticAberration>(true);
            _flashChromatic.intensity.Override(parryChromatic);

            _hurtVolume = CreateVolume("HitFeel Hurt", out _hurtProfile);
            Vignette vignette = _hurtProfile.Add<Vignette>(true);
            vignette.color.Override(hurtVignetteColor);
            vignette.intensity.Override(hurtVignetteIntensity);
            vignette.smoothness.Override(0.5f);
            _hurtProfile.Add<ChromaticAberration>(true).intensity.Override(hurtChromatic);
        }

        private void OnDestroy()
        {
            if (_flashVolume != null) Destroy(_flashVolume.gameObject);
            if (_hurtVolume != null) Destroy(_hurtVolume.gameObject);
            if (_flashProfile != null) Destroy(_flashProfile);
            if (_hurtProfile != null) Destroy(_hurtProfile);
        }

        private static Volume CreateVolume(string name, out VolumeProfile profile)
        {
            var go = new GameObject(name);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 50f;
            volume.weight = 0f;
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            volume.profile = profile;
            return volume;
        }

        private void OnAttackHit(PlayerAttackDefinition attack, Vector3 point)
        {
            float t = Mathf.InverseLerp(lightDamage, heavyDamage, attack.damage);
            if (attack.input == PlayerAttackInput.Heavy) t = Mathf.Max(t, heavyInputMinimumBlend);
            bool riposte = attack.name == PlayerAttackDefinition.RiposteName;
            Play(riposte ? riposteHit : Blend(lightHit, heavyHit, t));

            Vector3 toHit = Flat(point - transform.position);
            float weight = riposte ? 1f : t;
            if (_player != null) _player.KickCamera(toHit * Mathf.Lerp(hitKick.x, hitKick.y, weight), kickDuration);
            if (riposte) Flash(0.8f, parryFlashDuration);
            else if (t >= heavyInputMinimumBlend && heavyHitChromatic > 0f) Flash(0f, kickDuration, heavyHitChromatic / Mathf.Max(0.01f, parryChromatic));
            FlinchBoss(toHit, weight * (riposte ? 1.3f : 1f));
        }

        private void OnDamageTaken(DamageInfo damage)
        {
            float t = Mathf.InverseLerp(lightIncoming, heavyIncoming, damage.Amount);
            Play(Blend(lightDamaged, heavyDamaged, t));
            // 맞은 방향(공격자 → 나)으로 카메라가 밀리고 화면 가장자리가 붉게 번진다.
            Vector3 push = Flat(damage.Direction);
            if (push.sqrMagnitude < 0.01f && damage.Source != null) push = Flat(transform.position - damage.Source.transform.position);
            if (_player != null) _player.KickCamera(push * Mathf.Lerp(hurtKick.x, hurtKick.y, t), kickDuration * 1.3f);
            _hurt = Mathf.Max(_hurt, Mathf.Lerp(0.6f, 1f, t));
            _hurtLength = hurtDuration;
        }

        private void OnParried(DamageInfo incoming)
        {
            Play(parry);
            // 칼이 맞부딪힌 순간: 하얀 섬광 + 색수차 + 앞으로 찍히는 카메라 + 보스가 크게 젖혀짐.
            Flash(1f, parryFlashDuration);
            Vector3 toBoss = incoming.Source != null ? Flat(incoming.Source.transform.position - transform.position) : transform.forward;
            if (_player != null) _player.KickCamera(toBoss * parryKick, kickDuration * 1.2f);
            FlinchBoss(toBoss, 1.4f);
        }

        // exposure: 섬광 세기(0이면 색수차만). chromaticScale: 색수차를 parryChromatic의 몇 배로 줄지.
        private void Flash(float exposure, float duration, float chromaticScale = 1f)
        {
            if (_flashVolume == null) return;
            ColorAdjustments color;
            if (_flashProfile.TryGet(out color))
                color.postExposure.Override(Mathf.Lerp(0.45f, parryFlashExposure, exposure));
            _flashChromatic.intensity.Override(parryChromatic * Mathf.Clamp01(chromaticScale));
            _flash = 1f;
            _flashLength = duration;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.zero;
        }

        // ---------- 보스 몸 반응 ----------
        // 보스 골반(Hips)을 맞은 방향으로 살짝 밀고 뒤로 젖힌다(보스 Animator는 보스 루트에 붙어 있어 몸 전체를 옮길 수 없다).
        // 애니메이션이 매 프레임 뼈를 다시 쓰면 그 자세 위에 더하고, 시간이 멈춰(히트스톱 0·일시정지) 다시 쓰지 않은 프레임은
        // 지난 프레임의 원래 자세를 기준으로 삼아 오프셋이 쌓이지 않게 한다.
        private void FlinchBoss(Vector3 direction, float weight)
        {
            if (!FindBossBody() || direction.sqrMagnitude < 0.01f) return;
            float w = Mathf.Clamp01(weight);
            float over = Mathf.Max(0f, weight - 1f);
            _flinchDirection = direction;
            _flinchDistance = Mathf.Lerp(bossFlinchDistance.x, bossFlinchDistance.y, w) * (1f + over);
            _flinchTilt = Mathf.Lerp(bossFlinchTilt.x, bossFlinchTilt.y, w) * (1f + over);
            _flinchLength = bossFlinchDuration * (1f + over);
            _flinchRemaining = _flinchLength;
        }

        private bool FindBossBody()
        {
            if (_bossBody != null) return true;
            if (_boss == null) _boss = FindAnyObjectByType<BossController>();
            if (_boss == null) return false;
            Animator animator = _boss.GetComponentInChildren<Animator>();
            if (animator == null || !animator.isHuman) return false;
            _bossBody = animator.GetBoneTransform(HumanBodyBones.Hips);
            return _bossBody != null;
        }

        private void LateUpdate()
        {
            if (!PauseMenu.IsPaused)
            {
                // 한 프레임이 길게 걸려도(끊김·캡처) 연출이 통째로 건너뛰지 않게 한 프레임에 줄이는 양을 제한한다.
                float dt = Mathf.Min(Time.unscaledDeltaTime, 1f / 30f);
                _flash = Mathf.Max(0f, _flash - dt / Mathf.Max(0.01f, _flashLength));
                _hurt = Mathf.Max(0f, _hurt - dt / Mathf.Max(0.01f, _hurtLength));
                if (_flinchRemaining > 0f) _flinchRemaining = Mathf.Max(0f, _flinchRemaining - dt);
            }
            ApplyScreen();
            ApplyFlinch();
        }

        private void ApplyScreen()
        {
            // 처음에 가장 세고 빠르게 빠진다.
            if (_flashVolume != null) _flashVolume.weight = _flash * _flash;
            if (_hurtVolume != null) _hurtVolume.weight = _hurt;
        }

        private bool _flinchApplied;
        private float _flinchLength = 1f;
        private Vector3 _writtenPosition;
        private Quaternion _writtenRotation;

        private void ApplyFlinch()
        {
            if (_bossBody == null) return;
            // 지난 프레임에 내가 쓴 값이 그대로면 애니메이션이 뼈를 다시 쓰지 않은 것 → 원래 자세로 되돌린 뒤 다시 더한다.
            if (_flinchApplied && _bossBody.localPosition == _writtenPosition && _bossBody.localRotation == _writtenRotation)
            {
                _bossBody.localPosition = _bossBodyRestPosition;
                _bossBody.localRotation = _bossBodyRestRotation;
            }
            _flinchApplied = false;
            if (_flinchRemaining <= 0f) return;

            _bossBodyRestPosition = _bossBody.localPosition;
            _bossBodyRestRotation = _bossBody.localRotation;
            float t = 1f - _flinchRemaining / Mathf.Max(0.01f, _flinchLength);
            // 한 프레임 만에 밀렸다가(맞는 순간) 남은 시간 동안 돌아온다.
            float envelope = (1f - t) * (1f - t);
            _bossBody.position += _flinchDirection * (_flinchDistance * envelope);
            _bossBody.rotation = Quaternion.AngleAxis(_flinchTilt * envelope, Vector3.Cross(Vector3.up, _flinchDirection)) * _bossBody.rotation;
            _writtenPosition = _bossBody.localPosition;
            _writtenRotation = _bossBody.localRotation;
            _flinchApplied = true;
        }

        private void EndFlinch()
        {
            if (_flinchApplied && _bossBody != null && _bossBody.localPosition == _writtenPosition && _bossBody.localRotation == _writtenRotation)
            {
                _bossBody.localPosition = _bossBodyRestPosition;
                _bossBody.localRotation = _bossBodyRestRotation;
            }
            _flinchApplied = false;
            _flinchRemaining = 0f;
        }

        public void PlayPhaseShiftSlowMotion() =>
            Play(new Feel { hitStop = 0.5f, hitStopTimeScale = 0.5f });

        private void Play(Feel feel)
        {
            if (_player != null) _player.ShakeCamera(feel.shake, feel.shakeDuration);
            if (feel.hitStop <= 0f || SimulationMode.Active) return;
            // 겹치면 더 늦게 끝나는 쪽만 남긴다(멈춤이 쌓여 길어지지 않게).
            _stopUntil = Mathf.Max(_stopUntil, Time.unscaledTime + feel.hitStop);
            Time.timeScale = _stopping ? Mathf.Min(Time.timeScale, feel.hitStopTimeScale) : feel.hitStopTimeScale;
            _stopping = true;
        }

        private void Update()
        {
            if (_stopping && Time.unscaledTime >= _stopUntil) EndStop();
        }

        private void EndStop()
        {
            if (!_stopping) return;
            _stopping = false;
            if (!PauseMenu.IsPaused) Time.timeScale = 1f;
        }

        private static Feel Blend(Feel a, Feel b, float t) => new Feel
        {
            hitStop = Mathf.Lerp(a.hitStop, b.hitStop, t),
            hitStopTimeScale = Mathf.Lerp(a.hitStopTimeScale, b.hitStopTimeScale, t),
            shake = Mathf.Lerp(a.shake, b.shake, t),
            shakeDuration = Mathf.Lerp(a.shakeDuration, b.shakeDuration, t),
        };
    }
}
