namespace ProjectSoullike
{
    // 학습·자동 평가용 시뮬레이션 모드(보스 디렉터 학습 씬이 켠다).
    // 켜져 있으면 게임 속도를 바꾸는 연출(히트스톱·반격 슬로모션)과 데이터 수집용 캘리브레이션 판을 끈다.
    // 학습기가 정한 시간 배율이 연출 때문에 1로 돌아가지 않게 하려는 것이다. 전조·판정·피해는 그대로다.
    public static class SimulationMode
    {
        public static bool Active { get; set; }
    }

    // 다음 패턴까지의 대기 시간을 외부(보스 디렉터)가 정할 때 BossController.PatternPolicy가 함께 구현한다.
    // 돌려준 값은 난이도 손잡이 범위(BossTuning.PatternIntervalMin~Max)로 자른다. 음수면 손잡이 값을 그대로 쓴다.
    public interface IBossPacingPolicy
    {
        float NextPatternInterval(BossController boss, float defaultInterval);
    }
}
