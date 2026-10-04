using System;
using System.Collections.Generic;
using System.Linq;

namespace MonsterAdventure.Core
{
    /// <summary>SPEC "월드": 풀숲 조우와 야생 몬스터 결정.</summary>
    public static class WildEncounter
    {
        public const double GrassChance = 0.14;
        public const double WaterChance = 0.14;               // 물 타일도 풀숲과 같은 확률
        public const string LateGameBaseForm = "뿔불이";      // 마을 중심에서 이 거리 미만이면 등장하지 않는다
        public const double LateGameDistance = 11;
        public const string WaterType = "water";

        /// <summary>한 칸 이동을 마쳤을 때 조우가 일어나는지. 풀숲·물이 아니면 난수를 소비하지 않는다.</summary>
        public static bool ShouldEncounter(Tile tile, IRng rng) =>
            (tile == Tile.TallGrass || tile == Tile.Water) && rng.NextDouble() < (tile == Tile.Water ? WaterChance : GrassChance);

        /// <summary>야생 후보(기본형). 물 타일이면 물 타입만, 풀숲이면 물 타입을 뺀 나머지.
        /// 웹의 wildPool(d, water)과 같은 순서·규칙이라 골든 테스트가 그대로 유효하다.</summary>
        public static List<SpeciesData> Pool(GameData data, double distance, bool water) =>
            data.BaseForms
                .Where(s => (distance >= LateGameDistance || s.Name != LateGameBaseForm) && (s.Type == WaterType) == water)
                .ToList();

        /// <summary>야생 레벨 = clamp(2 + floor(거리/3.4) + rand(-1..1), 2, 40)</summary>
        public static int RollLevel(double distance, IRng rng) =>
            Math.Min(40, Math.Max(2, 2 + (int)Math.Floor(distance / 3.4) + rng.Range(-1, 1)));

        public static Monster Generate(GameData data, int tileX, int tileY, IRng rng, bool water = false)
        {
            double d = WorldMap.DistanceFromVillage(tileX, tileY);
            int level = RollLevel(d, rng);
            var species = rng.Pick(Pool(data, d, water));
            // 레벨이 진화 레벨 이상이면 진화형으로 등장
            while (species.Evolve != null && level >= species.Evolve.Level) species = data.GetSpecies(species.Evolve.To);
            return Monster.Create(data, species.Id, level);
        }
    }
}
