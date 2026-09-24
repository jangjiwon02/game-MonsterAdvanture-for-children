namespace MonsterAdventure.Core
{
    public enum Side { Player, Enemy }

    public enum BattleOutcome { Ongoing, Win, Lose, Fled, Caught }

    // 플레이어가 한 턴에 고르는 행동 (SPEC "턴 진행": 싸운다 / 가방 / 몬스터 교체 / 도망)
    public abstract class BattleAction { }
    public sealed class MoveAction : BattleAction { public readonly string MoveId; public MoveAction(string moveId) { MoveId = moveId; } }
    /// <summary>
    /// 볼 던지기. hit=false 면 빗나가서 볼만 소모하고 이어서 적이 공격한다(BallMissed).
    /// throwQuality(1.0~1.5, <see cref="BallisticThrow.ThrowQualityMultiplier"/>)는 CaptureMode.ThreeShake 에서만 쓰인다.
    /// </summary>
    public sealed class BallAction : BattleAction
    {
        public readonly bool Hit;
        public readonly double ThrowQuality;
        public BallAction(bool hit = true, double throwQuality = 1.0) { Hit = hit; ThrowQuality = throwQuality; }
    }
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
        // 여기부터 옵트인 기능(옵저버·볼 투척)에서만 나온다 — 기존 값은 그대로 두고 끝에만 추가한다.
        BallMissed,         // 볼이 목표를 빗나감(BallThrown 다음, 포획 판정 없음)
        WeatherStarted,     // Name(날씨 id: sunny/rain/sandstorm), Amount(지속 턴), Text
        WeatherEnded,       // Name, Text
        ResidualDamage,     // Side(맞은 쪽), Amount, Name(원인 id), Text — 날씨 피해 등 턴 끝 피해
        ResidualHeal,       // Side(회복한 쪽), Amount(실제 회복량), Name(원인 id), Text — 도구·특성 회복
        ObserverNotice,     // Side, Name(특성/도구 id), Text — "○○의 특성이 발동했다" 같은 안내 문구
    }

    /// <summary>
    /// 배틀 진행 상태(결정적 FSM). AwaitingAction → ResolvingTurn → (AwaitingReplacement → ResolvingTurn) → AwaitingAction | Ended.
    /// 이벤트 이터레이터를 끝까지 소비해야 ResolvingTurn 이 AwaitingAction 으로 돌아온다.
    /// </summary>
    public enum BattlePhase { AwaitingAction, ResolvingTurn, AwaitingReplacement, Ended }

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
        /// <summary>날씨·특성·도구 id 등 새 이벤트의 이름표.</summary>
        public string Name;
        /// <summary>UI 가 그대로 띄울 수 있는 한국어 문장(새 이벤트 전용, 없으면 null).</summary>
        public string Text;

        public override string ToString() => $"{Kind}" + (Kind == BattleEventKind.Damage || Kind == BattleEventKind.MoveUsed || Kind == BattleEventKind.Fainted || Kind == BattleEventKind.Missed
            || Kind == BattleEventKind.ResidualDamage || Kind == BattleEventKind.ResidualHeal || Kind == BattleEventKind.ObserverNotice ? $"({Side})" : "");
    }
}
