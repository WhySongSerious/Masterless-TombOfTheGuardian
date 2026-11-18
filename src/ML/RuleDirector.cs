using System.Collections.Generic;
using UnityEngine;

namespace ProjectSoullike
{
    // 비교 조건 B: 규칙 디렉터. 학습 없이 "체력 차"만 보고 몰아치거나 늦춘다(PPO 디렉터와 같은 행동 공간).
    //   플레이어가 앞서면(플레이어 체력 비율 − 보스 체력 비율 > 0.15) 짧은 대기 + 어려운 패턴(점수 가중치 높은 쪽)
    //   플레이어가 밀리면(< −0.15) 긴 대기 + 쉬운 패턴, 그 사이면 기본 비중 추첨 + 기본 대기
    //   같은 패턴 3연속은 피한다.
    public sealed class RuleDirector : IBossPatternPolicy, IBossPacingPolicy, IBossPressureSource
    {
        public const float Lead = 0.15f;
        private readonly BossController _boss;
        private float _nextInterval = -1f;
        private float _pressure = CombatScore.PressureDefault;
        private int _last1, _last2;

        public RuleDirector(BossController boss) => _boss = boss;

        public float ScorePressure => _pressure;

        public BossPatternSpec Choose(BossPatternContext context)
        {
            if (context.Boss != _boss || context.Candidates.Count == 0) return null;
            float lead = context.PlayerHealthRatio - context.BossHealthRatio;
            int mode = lead > Lead ? 1 : lead < -Lead ? -1 : 0;

            var options = new List<BossPatternSpec>(context.Candidates);
            if (options.Count > 1 && _last1 == _last2) options.RemoveAll(s => s.Id == _last1);
            BossPatternSpec chosen;
            if (mode == 0)
            {
                chosen = Draw(context, options);
            }
            else
            {
                options.Sort((a, b) => ScorePatternWeights.For(a.Id).CompareTo(ScorePatternWeights.For(b.Id)));
                // 몰아칠 때는 가장 어려운 쪽 절반, 늦출 때는 가장 쉬운 쪽 절반에서 고른다.
                int half = Mathf.Max(1, (options.Count + 1) / 2);
                int start = mode > 0 ? options.Count - half : 0;
                chosen = options[start + Random.Range(0, half)];
            }

            float interval = mode > 0 ? BossDirectorAgent.Intervals[0] : mode < 0 ? BossDirectorAgent.Intervals[4] : BossDirectorAgent.Intervals[1];
            _nextInterval = interval;
            _pressure = BossDirectorAgent.PressureFor(interval);
            _last2 = _last1;
            _last1 = chosen.Id;
            return chosen;
        }

        private static BossPatternSpec Draw(BossPatternContext context, List<BossPatternSpec> options)
        {
            float total = 0f;
            foreach (BossPatternSpec spec in options) total += Weight(context, spec);
            float roll = Random.value * Mathf.Max(total, 1e-4f);
            foreach (BossPatternSpec spec in options)
            {
                roll -= Weight(context, spec);
                if (roll <= 0f) return spec;
            }
            return options[options.Count - 1];
        }

        private static float Weight(BossPatternContext context, BossPatternSpec spec)
        {
            int index = context.Candidates.IndexOf(spec);
            return index >= 0 && index < context.Weights.Count ? context.Weights[index] : 1f;
        }

        public float NextPatternInterval(BossController boss, float defaultInterval)
        {
            if (boss != _boss || _nextInterval < 0f) return -1f;
            float interval = _nextInterval;
            _nextInterval = -1f;
            return interval;
        }
    }
}
