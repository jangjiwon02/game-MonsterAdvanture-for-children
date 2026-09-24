namespace MonsterAdventure.Core
{
    public enum Side { Player, Enemy }

    public enum BattleOutcome { Ongoing, Win, Lose, Fled, Caught }

    // 플레이어가 한 턴에 고르는 행동 (SPEC "턴 진행": 싸운다 / 가방 / 몬스터 교체 / 도망)
    public abstract class BattleAction { }
    public sealed class MoveAction : BattleAction { public readonly string MoveId; public MoveAction(string moveId) { MoveId = moveId; } }
    public sealed class BallAction : BattleAction { }
    public sealed class PotionAction : BattleAction { public readonly int PartyIndex; public PotionAction(int partyIndex) { PartyIndex = partyIndex; } }
    public sealed class SwitchAction : BattleAction { public readonly int PartyIndex; public SwitchAction(int partyIndex) { PartyIndex = partyIndex; } }
    public sealed class FleeAction : BattleAction { }

    public enum BattleEventKind
    {
        MoveUsed,           // Side, MoveId
        Missed,             // Side(공격한 쪽)
        Damage,             // Side(맞은 쪽), Amount, Multiplier, Critical
        Fainted,            // Side
        ExpGained,          // Amount
        Growth,             // Growth (레벨업/기술/진화)
        MoneyGained,        // Amount
        SwitchOut,          // 돌아와, ○○!
        SwitchIn,           // 가라! ○○!
        ReplacementNeeded,  // 플레이어 몬스터가 기절 → UI 가 ChooseReplacement 를 호출해야 다음으로 진행
        PotionUsed,         // PartyIndex, Amount(실제 회복량)
        BallThrown,
        CatchAttempt,       // Catch (흔들림 횟수·성공 여부)
        Caught,             // SentToBox
        FleeSucceeded,
        FleeFailed,
        BlackedOut,         // 전멸
    }

    /// <summary>
    /// BattleSession 이 낸 사건 하나. 이벤트는 모델이 그 상태로 바뀐 직후에 나오므로(지연 평가),
    /// UI 는 이벤트를 재생하는 동안 모델 값을 그대로 읽어도 화면과 어긋나지 않는다.
    /// </summary>
    public sealed class BattleEvent
    {
        public BattleEventKind Kind;
        public Side Side;
        public string MoveId;
        public int Amount;
        public double Multiplier = 1.0;
        public bool Critical;
        public int PartyIndex;
        public CatchResult Catch;
        public bool SentToBox;
        public GrowthEvent Growth;

        public override string ToString() => $"{Kind}" + (Kind == BattleEventKind.Damage || Kind == BattleEventKind.MoveUsed || Kind == BattleEventKind.Fainted || Kind == BattleEventKind.Missed ? $"({Side})" : "");
    }
}
