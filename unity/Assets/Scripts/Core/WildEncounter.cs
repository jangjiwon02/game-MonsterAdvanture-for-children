using System;
using System.Collections.Generic;
using System.Linq;

namespace MonsterAdventure.Core
{
    /// <summary>SPEC "월드": 풀숲 조우와 야생 몬스터 결정.</summary>
    public static class WildEncounter
    {
        public const double GrassChance = 0.14;
        public const string LateGameBaseForm = "뿔불이";      // 마을 중심에서 이 거리 미만이면 등장하지 않는다
        public const double LateGameDistance = 11;

        /// <summary>한 칸 이동을 마쳤을 때 조우가 일어나는지. 풀숲이 아니면 난수를 소비하지 않는다.</summary>
        public static bool ShouldEncounter(Tile tile, IRng rng) => tile == Tile.TallGrass && rng.NextDouble() < GrassChance;

        /// <summary>야생 레벨 = clamp(2 + floor(거리/3.4) + rand(-1..1), 2, 40)</summary>
        public static int RollLevel(double distance, IRng rng) =>
            Math.Min(40, Math.Max(2, 2 + (int)Math.Floor(distance / 3.4) + rng.Range(-1, 1)));

        public static Monster Generate(GameData data, int tileX, int tileY, IRng rng)
        {
            double d = WorldMap.DistanceFromVillage(tileX, tileY);
            int level = RollLevel(d, rng);
            List<SpeciesData> pool = data.BaseForms.Where(s => d >= LateGameDistance || s.Name != LateGameBaseForm).ToList();
            var species = rng.Pick(pool);
            // 레벨이 진화 레벨 이상이면 진화형으로 등장
            while (species.Evolve != null && level >= species.Evolve.Level) species = data.GetSpecies(species.Evolve.To);
            return Monster.Create(data, species.Id, level);
        }
    }
}
