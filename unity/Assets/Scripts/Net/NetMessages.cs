using System.Collections.Generic;
using MonsterAdventure.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MonsterAdventure.Net
{
    /// <summary>맵 위의 다른 트레이너 모습(전투 파티가 아니라 겉모습만).</summary>
    public sealed class TrainerInfo
    {
        public int Id; public string Name; public int SpeciesId; public int X; public int Y; public int Dir;
        /// <summary>겉모습 색 번호(PlayerArt 팔레트). 접속자마다 서버가 겹치지 않게 정해 준다.</summary>
        public int Color;
    }

    /// <summary>StateJson: 일반 게임 저장(SaveSerializer.ToJson)을 그대로 들고 들어온다 — 있으면 서버는 그걸 이 사람의 계정으로 쓴다
    /// (일반 게임에서 키운 몬스터·레벨로 그대로 대결). 없으면(전용 서버 등) 예전처럼 서버가 이름으로 저장 계정을 찾는다.</summary>
    public sealed class HelloMessage { public string Name; public int SpeciesId; public string StateJson; }
    /// <summary>StateJson 은 SaveSerializer.ToJson 그대로(파티·가방·소지금 등) — 월드 메뉴(가방·몬스터)·상점·센터가
    /// 이걸로 로컬에 계정을 그대로 들고 있다가, 바뀌면 <see cref="UpdateAccountMessage"/> 로 되돌려 보낸다.</summary>
    public sealed class WelcomeMessage { public int Id; public int X; public int Y; public int Dir; public List<TrainerInfo> Others; public int Money; public bool BonusGranted; public string StateJson; public int Color; }
    /// <summary>서버가 계정을 바꾼 뒤(대결 경험치·레벨업 등) 최신 계정을 되돌려 보낸다 — 클라이언트는 이걸 일반 게임 저장 파일에 그대로 쓴다.</summary>
    public sealed class AccountUpdatedMessage { public string StateJson; }
    /// <summary>클라이언트가 자기 계정(가방·파티·소지금)을 바꿨을 때(상점 구매, 상처약 사용 등) 서버에 되돌려 보낸다.
    /// 이동처럼 클라이언트를 신뢰하는 모델이지만, 서버는 그래도 항상 SaveSerializer.FromJson 으로 검증한다.</summary>
    public sealed class UpdateAccountMessage { public string StateJson; }
    public sealed class JoinedMessage { public TrainerInfo Info; }
    public sealed class LeftMessage { public int Id; }
    public sealed class MoveMessage { public int Dir; }
    public sealed class MovedMessage { public int Id; public int X; public int Y; public int Dir; }
    public sealed class ChallengeRequestMessage { public int TargetId; }
    public sealed class ChallengeOfferMessage { public int FromId; public string FromName; }
    public sealed class ChallengeResponseMessage { public bool Accept; }
    public sealed class ChallengeResultMessage { public int OtherId; public bool Accepted; }
    public sealed class ErrorMessage { public string Reason; }

    /// <summary>대결 중인 몬스터 한 마리의 정보. 내 파티는 전부, 상대는 출전 중인 것(또는 방금 나온 것)만 보낸다.</summary>
    public sealed class DuelMonsterInfo
    {
        public int SpeciesId; public int Level; public int Exp; public int Hp; public int MaxHp; public List<string> Moves;
    }

    /// <summary>수신자 기준으로 이미 뒤집어 보낸다. Party 는 내 파티(임시 풀피 사본, 출전은 항상 첫 칸), Opponent 는 상대의 출전 몬스터.</summary>
    public sealed class DuelStartMessage
    {
        public int OpponentId; public string OpponentName;
        public List<DuelMonsterInfo> Party; public int Potions; public DuelMonsterInfo Opponent;
    }

    /// <summary>클라이언트가 이번 라운드에 고른 행동. Type 은 <see cref="NetMsgType.DuelActionType"/> 값.</summary>
    public sealed class DuelActionMessage
    {
        public string Type; public string MoveId; public int PartyIndex;

        public static DuelActionMessage From(BattleAction a) => a switch
        {
            MoveAction m => new DuelActionMessage { Type = NetMsgType.DuelActionType.Move, MoveId = m.MoveId },
            PotionAction p => new DuelActionMessage { Type = NetMsgType.DuelActionType.Potion, PartyIndex = p.PartyIndex },
            SwitchAction s => new DuelActionMessage { Type = NetMsgType.DuelActionType.Switch, PartyIndex = s.PartyIndex },
            FleeAction _ => new DuelActionMessage { Type = NetMsgType.DuelActionType.Forfeit },
            _ => throw new System.ArgumentException("대결에서 보낼 수 없는 행동이다.", nameof(a)),
        };

        /// <summary>모르는 Type 이면 null.</summary>
        public BattleAction ToAction()
        {
            switch (Type)
            {
                case NetMsgType.DuelActionType.Move: return MoveId == null ? null : new MoveAction(MoveId);
                case NetMsgType.DuelActionType.Potion: return new PotionAction(PartyIndex);
                case NetMsgType.DuelActionType.Switch: return new SwitchAction(PartyIndex);
                case NetMsgType.DuelActionType.Forfeit: return new FleeAction();
                default: return null;
            }
        }
    }

    /// <summary>쓰러진 뒤 다음으로 내보낼 몬스터(내 파티 칸 번호).</summary>
    public sealed class DuelReplaceMessage { public int PartyIndex; }

    /// <summary>Side: 0 = 나, 1 = 상대(수신자 기준으로 서버가 미리 뒤집어 보낸다).
    /// PartyIndex 는 내 쪽 사건에만 채운다(상대는 -1). Monster 는 상대의 SwitchIn·PotionUsed 에서 그 시점의 모습.</summary>
    public sealed class DuelEventMessage
    {
        public string Kind; public int Side; public string MoveId;
        public int Amount; public double Multiplier = 1.0; public bool Critical;
        public int PartyIndex = -1; public bool TargetActive; public DuelMonsterInfo Monster;

        static string KindName(PvpEventKind k)
        {
            switch (k)
            {
                case PvpEventKind.MoveUsed: return NetMsgType.Duel.MoveUsed;
                case PvpEventKind.Missed: return NetMsgType.Duel.Missed;
                case PvpEventKind.Damage: return NetMsgType.Duel.Damage;
                case PvpEventKind.Fainted: return NetMsgType.Duel.Fainted;
                case PvpEventKind.SwitchOut: return NetMsgType.Duel.SwitchOut;
                case PvpEventKind.SwitchIn: return NetMsgType.Duel.SwitchIn;
                case PvpEventKind.PotionUsed: return NetMsgType.Duel.PotionUsed;
                case PvpEventKind.ReplacementNeeded: return NetMsgType.Duel.ReplacementNeeded;
                case PvpEventKind.Forfeit: return NetMsgType.Duel.Forfeit;
                default: return null;   // Ended 는 DuelEnded 메시지로 따로 보낸다
            }
        }

        /// <summary>서버 사건을 viewer 기준 메시지로 바꾼다. 보내지 않는 사건(Ended)이면 null.</summary>
        public static DuelEventMessage From(PvpEvent e, DuelSide viewer)
        {
            string kind = KindName(e.Kind);
            if (kind == null) return null;
            bool mine = e.Side == viewer;
            var m = new DuelEventMessage
            {
                Kind = kind, Side = mine ? 0 : 1, MoveId = e.MoveId, Amount = e.Amount, Multiplier = e.Multiplier, Critical = e.Critical,
                PartyIndex = mine ? e.PartyIndex : -1, TargetActive = e.TargetActive,
            };
            if (!mine && (e.Kind == PvpEventKind.SwitchIn || e.Kind == PvpEventKind.PotionUsed))
                m.Monster = new DuelMonsterInfo { SpeciesId = e.SpeciesId, Level = e.Level, Hp = e.Hp, MaxHp = e.MaxHp };
            return m;
        }
    }

    /// <summary>한 라운드(또는 교체 한 번)의 사건 묶음. 순서대로 재생한다.</summary>
    public sealed class DuelEventsMessage { public List<DuelEventMessage> Events; }

    /// <summary>이긴 쪽은 SPEC "승리 시 경험치"(패배한 종족의 baseExp·레벨 기준) 그대로를, 진 쪽은 친선 대결이라
    /// 벌칙 없이 같은 공식을 상대 기준으로 계산한 40%를 참가 보상으로 받는다 — 그래서 양쪽 다 ExpGained/Growth 가 채워질 수 있다.</summary>
    public sealed class DuelEndedMessage
    {
        public bool YouWon; public bool OpponentLeft;
        public int ExpGained;
        public List<GrowthEvent> Growth;
    }

    /// <summary>와이어 형식은 {"t":"<종류>","d":{...}} — 리플렉션/타입 이름을 신뢰하지 않고 화이트리스트로만 해석한다.</summary>
    /// <summary>방 검색용 응답. Probe 를 보내면 서버가 이걸 한 번 돌려주고 연결을 닫는다(입장하지 않는다).</summary>
    public sealed class RoomInfoMessage { public string Name; public int Players; }

    public static class NetMsgType
    {
        public const string Probe = "probe", RoomInfo = "roomInfo", AccountUpdated = "accountUpdated";
        public const string Hello = "hello", Welcome = "welcome", Joined = "joined", Left = "left",
            Move = "move", Moved = "moved", ChallengeRequest = "challengeRequest", ChallengeOffer = "challengeOffer",
            ChallengeResponse = "challengeResponse", ChallengeResult = "challengeResult", Error = "error",
            DuelStart = "duelStart", DuelAction = "duelAction", DuelReplace = "duelReplace", DuelEvents = "duelEvents", DuelEnded = "duelEnded",
            UpdateAccount = "updateAccount";
        /// <summary>DuelEventMessage.Kind 값(짧은 와이어 이름). Core 의 PvpEventKind 와 1:1 대응(Ended 제외).</summary>
        public static class Duel
        {
            public const string MoveUsed = "moveUsed", Missed = "missed", Damage = "damage", Fainted = "fainted",
                SwitchOut = "switchOut", SwitchIn = "switchIn", PotionUsed = "potionUsed",
                ReplacementNeeded = "replacementNeeded", Forfeit = "forfeit";
        }

        /// <summary>DuelActionMessage.Type 값.</summary>
        public static class DuelActionType
        {
            public const string Move = "move", Potion = "potion", Switch = "switch", Forfeit = "forfeit";
        }
    }

    public static class NetCodec
    {
        public static string Encode<T>(string type, T payload) =>
            JsonConvert.SerializeObject(new JObject { ["t"] = type, ["d"] = JObject.FromObject(payload) });

        /// <summary>형식이 아니면(JSON이 아니거나 t/d가 없으면) false.</summary>
        public static bool TryDecode(string wireJson, out string type, out JObject data)
        {
            type = null; data = null;
            JObject o;
            try { o = JObject.Parse(wireJson); } catch (JsonException) { return false; }
            if (o["t"] is not JValue tv || tv.Type != JTokenType.String) return false;
            if (o["d"] is not JObject d) return false;
            type = (string)tv; data = d;
            return true;
        }
    }
}
