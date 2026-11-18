using UnityEngine;
using UnityEngine.InputSystem;

namespace ProjectSoullike
{
    // 전투 패드 진동(DualSense/XInput 공통, PadRumble: 블루투스 DualSense도 울린다). 플레이어에 붙인다. CombatHitFeel이 없으면 자동으로 붙인다.
    // 원칙: 결과가 났을 때만 울린다. 보스 전조(텔레그래프)에는 울리지 않는다. 진동으로 미리 알려 주면 패턴 읽기 연습이 사라진다.
    // - 내가 때림: 맞혔을 때만. 약공은 가볍고 짧게, 강공·피니시는 더 묵직하게(피해 20 ~ 60 보간).
    //   패링 반격은 가장 세게, 히트스톱 동안 꽉 누른 뒤 풀어 준다(hold = CombatHitFeel 반격 히트스톱과 맞춤).
    //   차지 강공은 피니시와 같은 세기, 킥은 저주파만 짧게 '퍽'(피해는 거의 없고 밀어내는 느낌).
    // - 내가 맞음: 보스 패턴마다 다르게.
    //   내려찍기·올려베기 = 낮고 길게 '쿵', 3연타 1·2타 = 짧게 '톡', 3타 찌르기·돌진 찌르기 = 날카롭고 세게.
    //   영체 검은 짧고, 거대 검에 맞으면 가장 길게. 보스 타격 ID(BossPatternTable)로 고른다.
    // - 패링 성공: 짧고 높은 '팅'(고주파 모터만). 실패·헛스윙·구르기에는 울리지 않는다.
    // - 쓰러짐: 묵직하게 울리고 길게 잦아든다.
    // 기본값(low/high/초)은 보스 패턴표 초안 값. 패턴표에 공격 ID별 진동 칸이 생기면 그 값으로 옮긴다.
    // 모터: low = 왼쪽 저주파(묵직함), high = 오른쪽 고주파(날카로움). 0 ~ 1.
    [DisallowMultipleComponent]
    public sealed class CombatHaptics : MonoBehaviour
    {
        [System.Serializable]
        public struct Pulse
        {
            [Range(0f, 1f)] public float low;
            [Range(0f, 1f)] public float high;
            [Tooltip("최대 세기로 유지하는 시간(실제 초)")]
            [Min(0f)] public float hold;
            [Tooltip("그 뒤 0으로 줄어드는 시간(실제 초)")]
            [Min(0f)] public float fade;

            public float Duration => hold + fade;
            public float Strength => Mathf.Max(low, high);
        }

        [SerializeField] private bool vibrationEnabled = true;
        [Tooltip("전체 세기 배율. 설정 화면이 생기면 여기에 연결한다")]
        [SerializeField, Range(0f, 1.5f)] private float intensity = 1f;

        [Header("내가 때림 (피해 20 ~ 60 사이를 보간)")]
        [SerializeField] private Pulse lightHit = new Pulse { low = 0.1f, high = 0.35f, hold = 0.03f, fade = 0.03f };
        [SerializeField] private Pulse heavyHit = new Pulse { low = 0.45f, high = 0.5f, hold = 0.06f, fade = 0.06f };
        [Tooltip("피니시(Light 3 → 강공)는 따로 한 단계 더 묵직하게")]
        [SerializeField] private Pulse finisherHit = new Pulse { low = 0.6f, high = 0.6f, hold = 0.08f, fade = 0.08f };
        [Tooltip("킥 적중: 칼이 아니라 몸으로 밀어내는 둔탁한 '퍽'")]
        [SerializeField] private Pulse kickHit = new Pulse { low = 0.7f, high = 0.05f, hold = 0.05f, fade = 0.1f };
        [SerializeField, Min(0f)] private float lightDamage = 20f;
        [SerializeField, Min(0f)] private float heavyDamage = 60f;

        [Header("패링 반격 적중 (가장 셈)")]
        [SerializeField] private Pulse riposteHit = new Pulse { low = 0.9f, high = 0.9f, hold = 0.16f, fade = 0.14f };

        [Header("내가 맞음 (보스 패턴별)")]
        [Tooltip("내려찍기: 낮고 길게 '쿵'")]
        [SerializeField] private Pulse overheadHurt = new Pulse { low = 0.8f, high = 0.3f, hold = 0.08f, fade = 0.17f };
        [Tooltip("3연타 1·2타: 짧게 '톡'")]
        [SerializeField] private Pulse comboHurt = new Pulse { low = 0.25f, high = 0.5f, hold = 0.04f, fade = 0.03f };
        [Tooltip("3연타 3타 찌르기·돌진 찌르기: 날카롭고 세게")]
        [SerializeField] private Pulse thrustHurt = new Pulse { low = 0.5f, high = 0.9f, hold = 0.08f, fade = 0.07f };
        [Tooltip("패턴을 모르는 피해(옛 보스 등): 피해량으로 톡 ~ 쿵 사이를 보간")]
        [SerializeField, Min(0f)] private float lightIncoming = 12f;
        [SerializeField, Min(0f)] private float heavyIncoming = 32f;

        [Header("페이즈 전환 거대 검 (가장 김)")]
        [SerializeField] private Pulse phaseShift = new Pulse { low = 1f, high = 0.6f, hold = 0.15f, fade = 0.3f };

        [Header("내가 쓰러짐 (마지막 피격 진동을 덮고 길게 잦아든다)")]
        [SerializeField] private Pulse death = new Pulse { low = 1f, high = 0.3f, hold = 0.1f, fade = 0.5f };

        [Header("패링 성공 '팅'")]
        [SerializeField] private Pulse parry = new Pulse { low = 0f, high = 0.8f, hold = 0.05f, fade = 0.04f };

        private KeyboardPlayerController _player;
        private PlayerHealth _health;
        private PlayerParry _parry;
        private Gamepad _pad;
        private Pulse _current;
        private float _startTime;
        private bool _playing;

        public bool VibrationEnabled
        {
            get => vibrationEnabled;
            set { vibrationEnabled = value; if (!value) Stop(); }
        }

        public float Intensity { get => intensity; set => intensity = Mathf.Clamp(value, 0f, 1.5f); }

        private void Awake()
        {
            _player = GetComponent<KeyboardPlayerController>();
            _health = GetComponent<PlayerHealth>();
            _parry = GetComponent<PlayerParry>();
            if (_parry == null && _player != null) _parry = gameObject.AddComponent<PlayerParry>();
        }

        private void OnEnable()
        {
            if (_player != null) _player.AttackHit += OnAttackHit;
            if (_health != null) _health.DamageTaken += OnDamageTaken;
            if (_health != null) _health.Died += OnDied;
            if (_parry != null) _parry.Parried += OnParried;
        }

        private void OnDisable()
        {
            if (_player != null) _player.AttackHit -= OnAttackHit;
            if (_health != null) _health.DamageTaken -= OnDamageTaken;
            if (_health != null) _health.Died -= OnDied;
            if (_parry != null) _parry.Parried -= OnParried;
            Stop();
        }

        // 창을 내리거나 일시정지하면 진동이 계속 남지 않게 끈다.
        private void OnApplicationFocus(bool focused) { if (!focused) Stop(); }
        private void OnApplicationPause(bool paused) { if (paused) Stop(); }

        private void OnAttackHit(PlayerAttackDefinition attack, Vector3 point) =>
            Play(attack.name == PlayerAttackDefinition.RiposteName ? riposteHit
                : attack.input == PlayerAttackInput.Kick ? kickHit
                : attack.name == "Finisher" || attack.charged ? finisherHit
                : Blend(lightHit, heavyHit, Mathf.InverseLerp(lightDamage, heavyDamage, attack.damage)));

        private void OnDamageTaken(DamageInfo damage) => Play(HurtPulse(damage));

        private void OnParried(DamageInfo incoming) => Play(parry);

        private void OnDied() => Play(death);

        // 보스 2페이즈 전환 등 큰 충격파 연출에서 부른다.
        public void PlayPhaseShift() => Play(phaseShift);

        private Pulse HurtPulse(DamageInfo damage)
        {
            // 보스 판정은 Active 구간에서 나므로 맞은 순간 보스의 현재 패턴·단계가 곧 나를 때린 공격이다.
            var boss = damage.Source != null ? damage.Source.GetComponent<BossController>() : null;
            if (boss != null)
            {
                // 패턴이 아니라 타격 ID로 고른다. 2페이즈 패턴은 같은 타격을 다른 순서로 이어 붙이기 때문이다.
                // 가로베기(10401)는 따로 정하지 않아 피해량 보간(톡 ~ 쿵 중간)을 쓴다.
                switch (boss.CurrentStrikeId)
                {
                    case BossPatternTable.Overhead:
                    case BossPatternTable.Upswing: return overheadHurt;
                    case BossPatternTable.FirstSlash:
                    case BossPatternTable.SecondSlash:
                    case BossPatternTable.SpiritSword: return comboHurt;
                    case BossPatternTable.Thrust:
                    case BossPatternTable.DashThrust: return thrustHurt;
                    case BossPatternTable.GiantSword: return phaseShift;
                }
            }
            return Blend(comboHurt, overheadHurt, Mathf.InverseLerp(lightIncoming, heavyIncoming, damage.Amount));
        }

        private void Play(Pulse pulse)
        {
            if (!vibrationEnabled || intensity <= 0f || pulse.Duration <= 0f) return;
            // 지금 울리는 것이 더 세고 아직 길게 남았으면 약한 진동으로 덮어쓰지 않는다(반격 직후 잔타격 등).
            if (_playing && Remaining() > pulse.Duration * 0.5f && CurrentStrength() > pulse.Strength) return;
            var pad = Gamepad.current;
            if (pad == null) return;
            if (_pad != null && _pad != pad) PadRumble.Stop(_pad);
            _pad = pad;
            _current = pulse;
            _startTime = Time.unscaledTime;
            _playing = true;
            Apply(1f);
        }

        // 히트스톱으로 timeScale이 0이 되어도 진동은 실제 시간으로 흐른다.
        private void Update()
        {
            if (!_playing) return;
            float elapsed = Time.unscaledTime - _startTime;
            if (elapsed >= _current.Duration) { Stop(); return; }
            float envelope = elapsed <= _current.hold || _current.fade <= 0f
                ? 1f
                : 1f - (elapsed - _current.hold) / _current.fade;
            Apply(envelope);
        }

        private float Remaining() => _current.Duration - (Time.unscaledTime - _startTime);

        private float CurrentStrength()
        {
            float elapsed = Time.unscaledTime - _startTime;
            float envelope = elapsed <= _current.hold || _current.fade <= 0f
                ? 1f
                : Mathf.Clamp01(1f - (elapsed - _current.hold) / _current.fade);
            return _current.Strength * envelope;
        }

        private void Apply(float envelope)
        {
            if (_pad == null) return;
            float k = Mathf.Clamp01(envelope) * intensity;
            PadRumble.Set(_pad, Mathf.Clamp01(_current.low * k), Mathf.Clamp01(_current.high * k));
        }

        private void Stop()
        {
            _playing = false;
            if (_pad != null) PadRumble.Stop(_pad);
            _pad = null;
        }

        private static Pulse Blend(Pulse a, Pulse b, float t) => new Pulse
        {
            low = Mathf.Lerp(a.low, b.low, t),
            high = Mathf.Lerp(a.high, b.high, t),
            hold = Mathf.Lerp(a.hold, b.hold, t),
            fade = Mathf.Lerp(a.fade, b.fade, t),
        };
    }
}
