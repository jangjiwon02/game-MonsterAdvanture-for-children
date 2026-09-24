using System;

namespace MonsterAdventure.Core
{
    public readonly struct DamageResult
    {
        public readonly bool Missed;
        public readonly int Damage;
        public readonly double Multiplier;   // 타입 상성 배율
        public readonly bool Critical;

        public DamageResult(bool missed, int damage, double multiplier, bool critical)
        {
            Missed = missed; Damage = damage; Multiplier = multiplier; Critical = critical;
        }
    }

    /// <summary>SPEC "데미지". 굴림 순서는 웹과 같다: 명중 → 급소 → 편차.</summary>
    public static class DamageCalc
    {
        public const double CriticalChance = 1.0 / 16.0;
        public const double CriticalMultiplier = 1.5;
        public const double StabMultiplier = 1.5;

        /// <summary>굴림이 정해진 뒤의 순수 계산.</summary>
        public static int Compute(int attackerLevel, int power, int attack, int defense,
                                  double stab, double typeMultiplier, bool critical, double variance)
        {
            double basePower = Math.Floor(
                Math.Floor(2.0 * attackerLevel / 5 + 2) * power * attack / defense / 50 + 2);
            double dmg = Math.Floor(basePower * stab * typeMultiplier * (critical ? CriticalMultiplier : 1.0) * variance);
            return (int)Math.Max(1, dmg);
        }

        public static DamageResult Roll(GameData data, Monster attacker, Monster defender, MoveData move, IRng rng)
        {
            if (rng.NextDouble() * 100 > move.Accuracy)
                return new DamageResult(true, 0, 1.0, false);

            string attackerType = attacker.Species(data).Type;
            double stab = attackerType == move.Type ? StabMultiplier : 1.0;
            double mult = data.TypeMultiplier(move.Type, defender.Species(data).Type);
            bool crit = rng.NextDouble() < CriticalChance;
            double variance = 0.85 + rng.NextDouble() * 0.15;

            int dmg = Compute(attacker.Level, move.Power, attacker.Attack, defender.Defense, stab, mult, crit, variance);
            // 웹 구현: 배율 0(면역)이면 데미지 0. 현재 데이터에는 면역이 없다.
            return new DamageResult(false, mult == 0 ? 0 : dmg, mult, crit);
        }
    }
}
