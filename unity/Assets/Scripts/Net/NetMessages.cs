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
    }

    public sealed class HelloMessage { public string Name; public int SpeciesId; }
    /// <summary>StateJson 은 SaveSerializer.ToJson 그대로(파티·가방·소지금 등) — 월드 메뉴(가방·몬스터)·상점·센터가
    /// 이걸로 로컬에 계정을 그대로 들고 있다가, 바뀌면 <see cref="UpdateAccountMessage"/> 로 되돌려 보낸다.</summary>
    public sealed class WelcomeMessage { public int Id; public int X; public int Y; public int Dir; public List<TrainerInfo> Others; public int Money; public bool BonusGranted; public string StateJson; }
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

    /// <summary>대결 중인 몬스터 한 마리의 공개 정보(양쪽 다 볼 수 있는 것).</summary>
    public sealed class DuelMonsterInfo
    {
        public int SpeciesId; public int Level; public int Hp; public int MaxHp; public List<string> Moves;
    }

    /// <summary>수신자 기준으로 이미 뒤집어 보낸다 — You/Opponent 이지 A/B 가 아니다.</summary>
    public sealed class DuelStartMessage
    {
        public int OpponentId; public string OpponentName;
        public DuelMonsterInfo You; public DuelMonsterInfo Opponent;
    }

    public sealed class DuelActionMessage { public string MoveId; }

    /// <summary>Side: 0 = 나, 1 = 상대(수신자 기준으로 서버가 미리 뒤집어 보낸다).</summary>
    public sealed class DuelEventMessage
    {
        public string Kind; public int Side; public string MoveId;
        public int Amount; public double Multiplier = 1.0; public bool Critical;
    }

    /// <summary>이긴 쪽은 SPEC "승리 시 경험치"(패배한 종족의 baseExp·레벨 기준) 그대로를, 진 쪽은 친선 대결이라
    /// 벌칙 없이 같은 공식을 상대 기준으로 계산한 40%를 참가 보상으로 받는다 — 그래서 양쪽 다 ExpGained/Growth 가 채워질 수 있다.</summary>
    public sealed class DuelEndedMessage
    {
        public bool YouWon; public bool OpponentLeft;
        public int ExpGained;
        public List<GrowthEvent> Growth;
    }

    /// <summary>와이어 형식은 {"t":"<종류>","d":{...}} — 리플렉션/타입 이름을 신뢰하지 않고 화이트리스트로만 해석한다.</summary>
    public static class NetMsgType
    {
        public const string Hello = "hello", Welcome = "welcome", Joined = "joined", Left = "left",
            Move = "move", Moved = "moved", ChallengeRequest = "challengeRequest", ChallengeOffer = "challengeOffer",
            ChallengeResponse = "challengeResponse", ChallengeResult = "challengeResult", Error = "error",
            DuelStart = "duelStart", DuelAction = "duelAction", DuelEvent = "duelEvent", DuelEnded = "duelEnded",
            UpdateAccount = "updateAccount";

        /// <summary>DuelEventMessage.Kind 값(짧은 와이어 이름). Core 의 DuelEventKind 와 1:1 대응.</summary>
        public static class Duel
        {
            public const string MoveUsed = "moveUsed", Missed = "missed", Damage = "damage", Fainted = "fainted";
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
