using System.Collections.Generic;
using UnityEngine;

namespace ProjectSoullike
{
    public enum RuleBotSkill { Novice, Intermediate, Expert }

    // 규칙 봇: 보스 디렉터 학습과 자동 평가의 상대. 사람과 같은 입력 층(IPlayerCommandSource)으로만 조작한다.
    // 보스의 전조·판정·경직을 읽어 막고(패링·구르기), 틈에 공격한다. 실력 단계는 세 가지 손잡이로 나눈다.
    //   방어 실패율: 전조를 보고도 아무것도 안 하거나 너무 일찍 눌러 맞는 비율
    //   패링 비율: 패링 가능한 공격을 패링으로 받는 비율(나머지는 구르기)
    //   공격성: 틈에서 실제로 치러 들어가는 비율과 공격 간격
    // 값은 11/15 시범 데이터(초보·중급·숙련 봇)의 피격·패링 경향에 맞춰 잡은 초안이다. 사람 기록으로 다시 맞춘다.
    public sealed class RuleBot : IPlayerCommandSource
    {
        private struct Profile
        {
            public float defenseFail;   // 방어 실패율
            public float earlyPress;    // 실패 중 "너무 일찍 누름" 비율(나머지는 무반응)
            public float parryRatio;    // 패링 가능한 공격을 패링으로 받을 확률
            public float aggression;    // 틈에서 공격하러 들어갈 확률
            public float attackGap;     // 공격 묶음 사이 최소 간격(초)
            public float reactionJitter; // 방어 타이밍 흔들림(초)
        }

        private static Profile For(RuleBotSkill skill)
        {
            switch (skill)
            {
                case RuleBotSkill.Novice:
                    return new Profile { defenseFail = 0.45f, earlyPress = 0.5f, parryRatio = 0.2f, aggression = 0.55f, attackGap = 2.2f, reactionJitter = 0.12f };
                case RuleBotSkill.Intermediate:
                    return new Profile { defenseFail = 0.25f, earlyPress = 0.4f, parryRatio = 0.45f, aggression = 0.75f, attackGap = 1.7f, reactionJitter = 0.07f };
                default:
                    return new Profile { defenseFail = 0.08f, earlyPress = 0.3f, parryRatio = 0.75f, aggression = 0.9f, attackGap = 1.3f, reactionJitter = 0.03f };
            }
        }

        // 학습·평가 실행 인자로 덮어쓰는 방어 타이밍(초, 전조 끝나기 전 몇 초에 누를지). 음수면 기본값.
        public static float DodgeLeadOverride = -1f, ParryLeadOverride = -1f;
        // 실행 인자로 실력 단계의 방어 실패율에 곱하는 값(1 = 그대로).
        public static float DefenseFailScale = 1f;

        private enum Act { Light, Heavy, Dodge, Parry, Riposte, Kick, LockOn }

        private readonly KeyboardPlayerController _p;
        private readonly BossController _b;
        private readonly GolemHealth _gh;
        private readonly PlayerStamina _stamina;
        private readonly Profile _profile;
        private readonly System.Random _rng;
        private readonly List<(float at, Act action, float hold)> _queue = new List<(float, Act, float)>();

        private BossAttackPhase _lastPhase;
        private int _lastStrike;
        private float _telegraphAt = -1f;
        private float _defendAt = -1f;
        private bool _defendPlanned;
        private bool _defendFails;
        private bool _parryPending;
        private bool _punish;
        private bool _ripostePressed;
        private float _riposteAimSince = -1f;
        private float _lockCooldown;
        private float _nextAttack;
        private float _heavyHoldUntil = -1f;
        private float _dodgeSlashAt = -1f;
        private int _combo;
        private int _side = 1;

        public RuleBotSkill Skill { get; }
        public string ParticipantId => "BOT_" + Skill.ToString().ToUpperInvariant();

        public RuleBot(KeyboardPlayerController player, BossController boss, RuleBotSkill skill, int seed)
        {
            _p = player;
            _b = boss;
            _gh = boss.GetComponent<GolemHealth>();
            _stamina = player.GetComponent<PlayerStamina>();
            Skill = skill;
            _profile = For(skill);
            _rng = new System.Random(seed);
        }

        private float Roll() => (float)_rng.NextDouble();

        private Vector2 ToInput(Vector3 world)
        {
            world.y = 0f;
            if (world.sqrMagnitude < 1e-6f) return Vector2.zero;
            world.Normalize();
            Camera cam = Camera.main;
            if (cam == null) return new Vector2(world.x, world.z);
            Vector3 f = cam.transform.forward; f.y = 0f; f.Normalize();
            Vector3 r = cam.transform.right; r.y = 0f; r.Normalize();
            return new Vector2(Vector3.Dot(world, r), Vector3.Dot(world, f));
        }

        private static void Press(ref PlayerCommands c, Act a)
        {
            switch (a)
            {
                case Act.Light: c.Light = true; break;
                case Act.Heavy: c.Heavy = true; c.HeavyHeld = true; break;
                case Act.Dodge: c.Dodge = true; break;
                case Act.Parry: c.Parry = true; break;
                case Act.Riposte: c.Riposte = true; break;
                case Act.Kick: c.Kick = true; break;
                case Act.LockOn: c.LockOn = true; break;
            }
        }

        public PlayerCommands ReadCommands(KeyboardPlayerController player)
        {
            var c = new PlayerCommands();
            if (_gh == null || !_gh.IsAlive) return c;
            Vector3 to = _b.transform.position - _p.transform.position;
            to.y = 0f;
            Fight(ref c, Time.time, to, to.magnitude);
            return c;
        }

        private void Fight(ref PlayerCommands c, float t, Vector3 to, float dist)
        {
            if (!_p.IsLockedOn && t > _lockCooldown && dist < 25f)
            {
                Press(ref c, Act.LockOn);
                _lockCooldown = t + 1.5f;
            }

            // 반격: 경직이 열리면 약점 방향으로 기울이고 잠깐 뒤 반격. 반격 창은 실제 시간으로 열린다.
            if (_b.IsRiposteOpen)
            {
                _queue.Clear();
                c.Move = _b.RiposteAim;
                float u = Time.unscaledTime;
                if (_riposteAimSince < 0f) { _riposteAimSince = u; _ripostePressed = false; }
                if (!_ripostePressed && u - _riposteAimSince >= 0.3f / Mathf.Max(0.01f, Time.timeScale))
                {
                    _ripostePressed = true;
                    Press(ref c, Act.Riposte);
                }
                return;
            }
            _riposteAimSince = -1f;

            BossAttackPhase phase = _b.AttackPhase;
            BossStrikeInfo info = _b.CurrentStrikeInfo;
            int strike = _b.CurrentStrikeId;
            bool newTelegraph = phase == BossAttackPhase.Telegraph && (_lastPhase != BossAttackPhase.Telegraph || strike != _lastStrike);
            if (newTelegraph)
            {
                _telegraphAt = t;
                _defendPlanned = false;
            }
            if (phase == BossAttackPhase.Recovery && _lastPhase != BossAttackPhase.Recovery) _punish = true;
            bool newActive = phase == BossAttackPhase.Active && (_lastPhase != BossAttackPhase.Active || strike != _lastStrike);
            _lastPhase = phase;
            _lastStrike = strike;

            // 차지 강공을 모으는 중(보스가 치려 하면 놓는다)
            if (t < _heavyHoldUntil && phase != BossAttackPhase.Telegraph && phase != BossAttackPhase.Active)
            {
                c.HeavyHeld = true;
                return;
            }
            _heavyHoldUntil = -1f;

            if (_parryPending)
            {
                if (phase == BossAttackPhase.Active) { _parryPending = false; Press(ref c, Act.Parry); return; }
                if (phase != BossAttackPhase.Telegraph) _parryPending = false;
                else return;
            }

            // 방어 계획: 전조를 처음 볼 때 이번 타격을 막을지(실력 단계의 실패율), 언제 누를지 정한다.
            if (info != null && !_defendPlanned && (phase == BossAttackPhase.Telegraph || newActive))
            {
                _defendPlanned = true;
                _defendFails = Roll() < _profile.defenseFail * DefenseFailScale;
                float total = _b.GetStrikeSettings(strike).telegraph;
                float parryLead = ParryLeadOverride >= 0f ? ParryLeadOverride : 0.07f;
                float dodgeLead = DodgeLeadOverride >= 0f ? DodgeLeadOverride : 0.1f;
                float lead = (info.Parryable ? parryLead : dodgeLead) + (Roll() * 2f - 1f) * _profile.reactionJitter;
                if (_defendFails && Roll() < _profile.earlyPress) lead += 0.35f; // 너무 일찍 눌러 판정 전에 끝난다
                else if (_defendFails) lead = float.NegativeInfinity;              // 무반응
                _defendAt = phase == BossAttackPhase.Telegraph ? _telegraphAt + Mathf.Max(0f, total - lead) : t;
            }

            if (_defendPlanned && _defendAt > 0f && t >= _defendAt && info != null &&
                (phase == BossAttackPhase.Telegraph || phase == BossAttackPhase.Active))
            {
                _defendAt = -1f;
                _queue.Clear();
                bool near = dist < 7.5f || info.Motion == BossStrikeMotion.Leap || info.Motion == BossStrikeMotion.Dash;
                if (!near) return;
                if (info.Parryable && info.Motion != BossStrikeMotion.Retreat && Roll() < _profile.parryRatio)
                {
                    if (phase == BossAttackPhase.Active || _defendFails) Press(ref c, Act.Parry);
                    else _parryPending = true;
                }
                else
                {
                    _side = -_side;
                    Vector3 sideways = Vector3.Cross(Vector3.up, to.normalized) * _side;
                    c.Move = ToInput(sideways + (dist < 3f ? -to.normalized * 0.4f : Vector3.zero));
                    Press(ref c, Act.Dodge);
                    if (Roll() < 0.4f * _profile.aggression) _dodgeSlashAt = t + 0.45f;
                }
                return;
            }

            // 구르기 베기(구른 직후 약공)
            if (_dodgeSlashAt > 0f && t >= _dodgeSlashAt && phase != BossAttackPhase.Telegraph)
            {
                _dodgeSlashAt = -1f;
                if (dist < 4.5f) { Press(ref c, Act.Light); _nextAttack = t + 0.9f; return; }
            }

            // 보스가 칠 준비 중이면 공격을 멈추고 기다린다
            if (phase == BossAttackPhase.Telegraph || phase == BossAttackPhase.Active || phase == BossAttackPhase.Guard)
            {
                _queue.Clear();
                if (phase == BossAttackPhase.Telegraph && dist < 2f) c.Move = ToInput(-to) * 0.5f;
                return;
            }

            for (int i = 0; i < _queue.Count; i++)
            {
                var q = _queue[i];
                if (q.at > t) continue;
                _queue.RemoveAt(i);
                Press(ref c, q.action);
                if (q.action == Act.Heavy && q.hold > 0f) _heavyHoldUntil = t + q.hold;
                return;
            }
            if (_queue.Count > 0) return;

            bool opening = phase == BossAttackPhase.Recovery || (phase == BossAttackPhase.None && !_b.IsPatternReady);
            if (dist > 2.5f)
            {
                if (opening || dist > 4.5f) { c.Move = ToInput(to); c.Sprint = dist > 7f; }
                return;
            }

            if (!opening || t < _nextAttack) return;
            if (phase == BossAttackPhase.Recovery && !_punish) return;
            _punish = false;
            if (Roll() > _profile.aggression) { _nextAttack = t + 0.6f; return; }
            // 숙련일수록 구르기 기력을 남겨 두고 친다(기력이 바닥나면 다음 공격을 못 피한다).
            if (_stamina != null && Skill != RuleBotSkill.Novice && _stamina.CurrentStamina < _stamina.MaxStamina * (Skill == RuleBotSkill.Expert ? 0.55f : 0.4f))
            { _nextAttack = t + 0.4f; return; }
            // 보스가 곧 칠 수 있는 대기 틈에는 짧게만 친다(긴 연타 중에 전조가 오면 못 막는다).
            if (phase != BossAttackPhase.Recovery && Skill == RuleBotSkill.Expert)
            { Plan(t, (0f, Act.Light, 0f)); _nextAttack = t + _profile.attackGap; return; }
            float gap = _profile.attackGap;
            switch (_combo++ % 6)
            {
                case 0: Plan(t, (0f, Act.Light, 0f), (0.42f, Act.Light, 0f), (0.84f, Act.Light, 0f)); _nextAttack = t + gap + 0.4f; break;
                case 1: Plan(t, (0f, Act.Heavy, 1.05f)); _nextAttack = t + gap + 0.8f; break;
                case 2: Plan(t, (0f, Act.Light, 0f), (0.42f, Act.Light, 0f), (0.9f, Act.Heavy, 0f)); _nextAttack = t + gap + 0.6f; break;
                case 3: Plan(t, (0f, Act.Kick, 0f), (0.7f, Act.Light, 0f)); _nextAttack = t + gap + 0.4f; break;
                case 4: Plan(t, (0f, Act.Heavy, 0f)); _nextAttack = t + gap; break;
                default: Plan(t, (0f, Act.Light, 0f), (0.42f, Act.Light, 0f)); _nextAttack = t + gap; break;
            }
        }

        private void Plan(float t, params (float delay, Act action, float hold)[] steps)
        {
            foreach (var s in steps) _queue.Add((t + s.delay, s.action, s.hold));
        }
    }
}
