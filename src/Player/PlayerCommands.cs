using UnityEngine;

namespace ProjectSoullike
{
    // 한 프레임의 플레이어 조작. 패드·키보드 입력이든 자동 플레이(규칙 봇)든 이 값 하나로 캐릭터를 움직인다.
    // 누름(Light·Heavy·Kick·Dodge·Parry·Riposte)은 이번 프레임에 새로 누른 것, Held는 누르고 있는 상태다.
    public struct PlayerCommands
    {
        // 이동 입력(카메라 기준, 크기 0~1). 락온 중이면 앞(+y)이 보스 쪽에 가깝다. 반격 조준에도 쓴다.
        public Vector2 Move;
        public bool Sprint;
        // 락온 켜기·끄기(누른 프레임).
        public bool LockOn;
        public bool Light;
        public bool Heavy;
        public bool HeavyHeld;
        public bool Kick;
        public bool Dodge;
        public bool Parry;
        public bool Riposte;
    }

    // 장치 입력 대신 조작을 넣는 쪽(자동 플레이·학습용 봇). KeyboardPlayerController.CommandSource에 넣으면
    // 매 프레임 이 값을 읽는다. 카메라 시점 회전은 계속 장치 입력을 따른다.
    public interface IPlayerCommandSource
    {
        PlayerCommands ReadCommands(KeyboardPlayerController player);
    }
}
