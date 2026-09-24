using System;
using Newtonsoft.Json;

namespace MonsterAdventure.Core
{
    /// <summary>
    /// SPEC "저장 항목"(party, box, balls, potions, money, dex, 플레이어 위치)을 JSON 으로 오가게 한다.
    /// 불러올 때는 종족 id·기술 id·레벨을 검증하고 능력치를 다시 계산한다(웹의 loadGame 처럼).
    /// 손상된 데이터는 예외 대신 null 을 돌려줘서, 호출한 쪽이 새 게임으로 넘어갈 수 있게 한다.
    /// </summary>
    public static class SaveSerializer
    {
        public const int Version = 1;

        sealed class Envelope
        {
            public int Version;
            public PlayerState State;
        }

        public static string ToJson(PlayerState state) =>
            JsonConvert.SerializeObject(new Envelope { Version = Version, State = state }, Formatting.Indented);

        public static PlayerState FromJson(GameData data, string json)
        {
            try
            {
                var env = JsonConvert.DeserializeObject<Envelope>(json);
                if (env == null || env.Version != Version || env.State == null) return null;
                return Validate(data, env.State) ? env.State : null;
            }
            catch (JsonException) { return null; }
        }

        static bool Validate(GameData data, PlayerState s)
        {
            if (s.Party == null || s.Box == null || s.Dex == null) return false;
            if (s.Party.Count < 1 || s.Party.Count > Capture.MaxPartySize) return false;
            foreach (var m in s.Party) if (!Fix(data, m)) return false;
            foreach (var m in s.Box) if (!Fix(data, m)) return false;
            foreach (int id in s.Dex) if (id < 0 || id >= data.Species.Count) return false;

            s.Balls = Math.Max(0, s.Balls);
            s.Potions = Math.Max(0, s.Potions);
            s.Money = Math.Max(0, s.Money);
            s.QuizDay = Math.Max(0, s.QuizDay);
            s.QuizAttempts = Math.Max(0, s.QuizAttempts);
            if (!WorldMap.InBounds(s.X, s.Y)) { s.X = WorldMap.VillageX; s.Y = WorldMap.VillageY + 1; }
            return true;
        }

        /// <summary>한 마리를 검증하고 능력치를 다시 계산한다. 이상하면 false.</summary>
        static bool Fix(GameData data, Monster m)
        {
            if (m == null || m.Moves == null) return false;
            if (m.SpeciesId < 0 || m.SpeciesId >= data.Species.Count) return false;
            if (m.Level < 1 || m.Level > Growth.MaxLevel) return false;
            if (m.Moves.Count < 1 || m.Moves.Count > Monster.MaxMoves) return false;
            foreach (var id in m.Moves) if (id == null || !data.HasMove(id)) return false;
            m.Exp = Math.Max(0, m.Exp);
            m.RecalcStats(data);
            m.Hp = Math.Min(m.MaxHp, Math.Max(0, m.Hp));
            return true;
        }
    }

    public enum ShopItem { Ball, Potion }

    /// <summary>SPEC "월드": 상점 — 몬스터볼 ₩100, 상처약 ₩80.</summary>
    public static class Shop
    {
        public const int BallPrice = 100;
        public const int PotionPrice = 80;

        public static int Price(ShopItem item) => item == ShopItem.Ball ? BallPrice : PotionPrice;

        /// <summary>돈이 모자라면 false(아무것도 바뀌지 않는다).</summary>
        public static bool TryBuy(PlayerState state, ShopItem item)
        {
            int price = Price(item);
            if (state.Money < price) return false;
            state.Money -= price;
            if (item == ShopItem.Ball) state.Balls++; else state.Potions++;
            return true;
        }
    }
}
