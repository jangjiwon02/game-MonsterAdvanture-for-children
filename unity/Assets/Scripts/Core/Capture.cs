using System;

namespace MonsterAdventure.Core
{
    public readonly struct CatchResult
    {
        public readonly bool Caught;
        public readonly int Shakes;          // 볼이 흔들린 횟수(성공 시 3)
        public readonly double Probability;

        public CatchResult(bool caught, int shakes, double probability)
        {
            Caught = caught; Shakes = shakes; Probability = probability;
        }
    }

    /// <summary>SPEC "포획", "도망".</summary>
    public static class Capture
    {
        public const int MaxPartySize = 6;

        /// <summary>p = clamp(catchRate * (1 - 0.6 * hp/maxHp) + 0.08, 0.05, 0.95)</summary>
        public static double Probability(double catchRate, int hp, int maxHp)
        {
            double hpFraction = (double)hp / maxHp;
            return Math.Min(0.95, Math.Max(0.05, catchRate * (1 - 0.6 * hpFraction) + 0.08));
        }

        /// <summary>성공 판정 → 실패 시 흔들림 0~2회(굴림 순서는 웹과 같다).</summary>
        public static CatchResult Attempt(GameData data, Monster target, IRng rng)
        {
            double p = Probability(target.Species(data).CatchRate, target.Hp, target.MaxHp);
            bool ok = rng.NextDouble() < p;
            int shakes = ok ? 3 : rng.Range(0, 2);
            return new CatchResult(ok, shakes, p);
        }
    }

    public static class Flee
    {
        /// <summary>p = clamp(0.5 + (내스피드 - 적스피드)/(합) * 0.5 + 시도횟수 * 0.15, 0.25, 1)</summary>
        public static double Chance(int playerSpeed, int enemySpeed, int tries) =>
            Math.Min(1.0, Math.Max(0.25,
                0.5 + (double)(playerSpeed - enemySpeed) / (playerSpeed + enemySpeed) * 0.5 + tries * 0.15));
    }
}
