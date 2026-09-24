using System;

namespace MonsterAdventure.Core
{
    /// <summary>BattleSession 의 포획 판정 방식. Legacy 는 기존 <see cref="Capture.Attempt"/>(성공 판정 1회 + 실패 시 흔들림 무작위).</summary>
    public enum CaptureMode { Legacy, ThreeShake }

    /// <summary>
    /// 흔들림 3회 포획 공식(원작식 "a 값 → 흔들림 검사" 구조를 이 프로젝트 데이터에 맞춰 스케일한 것).
    ///
    /// 1) a = ((3*maxHp - 2*hp) * catchRate * ball) / (3*maxHp) * status
    ///    데이터의 catchRate 는 0.1~0.8 확률형 실수라서 원작의 0~255 정수로 옮기지 않고 0~1 스케일 그대로 쓴다.
    ///    HP 항 (3*maxHp-2*hp)/(3*maxHp) 는 1/3(만피)~1(HP 0)이다.
    /// 2) 전체 성공 확률 P = clamp(a * 1.1 + 0.08, 0.05, 0.95)
    ///    기존 Capture.Probability(rate*(1-0.6*h)+0.08)와 견주어, 배율이 전부 1 일 때 두 값의 차이가
    ///    (새 값 - 기존 값)이 종족 catchRate 의 -3.3%(만피)~+10%(HP 0) 안에 들도록 1.1 배를 골랐다
    ///    (체력이 낮을수록 잘 잡히는 성질 동일, 상한·하한 동일).
    /// 3) 흔들림 검사 3번을 각각 p = P^(1/3) 로 굴린다. 세 번 다 통과해야 포획이라 전체 확률은 정확히 P 다.
    ///    k 번째(0부터)에서 처음 실패하면 Shakes = k(그 전까지 통과한 횟수), 다 통과하면 Caught·Shakes = 3.
    ///
    /// ball: 볼 계수(몬스터볼 1.0, 상위 볼은 더 크게), status: 상태이상 계수(관례상 수면 2.0, 마비/화상/독 1.5).
    /// 이 프로젝트엔 아직 둘 다 없으니 기본값 1.0. throwQuality(투척 품질 1.0~1.5)는 ball 에 곱해진다.
    /// </summary>
    public static class CatchMath
    {
        public const int ShakeChecks = 3;
        public const double Scale = 1.1;
        public const double Bonus = 0.08;
        public const double MinProbability = 0.05;
        public const double MaxProbability = 0.95;

        /// <summary>a 값(클램프 전). hp 는 0~maxHp 로 잘라서 쓴다.</summary>
        public static double CatchValue(double catchRate, int hp, int maxHp, double ballMultiplier = 1.0, double statusMultiplier = 1.0)
        {
            if (maxHp <= 0) throw new ArgumentOutOfRangeException(nameof(maxHp));
            int h = Math.Max(0, Math.Min(maxHp, hp));
            double ball = Math.Max(0.0, ballMultiplier), status = Math.Max(0.0, statusMultiplier);
            return (3.0 * maxHp - 2.0 * h) * catchRate * ball / (3.0 * maxHp) * status;
        }

        /// <summary>3번 다 통과할 전체 확률 P.</summary>
        public static double Probability(double catchRate, int hp, int maxHp, double ballMultiplier = 1.0, double statusMultiplier = 1.0) =>
            Math.Min(MaxProbability, Math.Max(MinProbability,
                CatchValue(catchRate, hp, maxHp, ballMultiplier, statusMultiplier) * Scale + Bonus));

        /// <summary>흔들림 검사 한 번의 통과 확률(P 의 세제곱근).</summary>
        public static double ShakeProbability(double totalProbability) => Math.Pow(totalProbability, 1.0 / ShakeChecks);

        /// <summary>전체 확률 P 로 흔들림 3회를 굴린다. 실패하는 즉시 멈추므로 난수는 실패한 검사까지만 쓴다.</summary>
        public static CatchResult RollShakes(double totalProbability, IRng rng)
        {
            double each = ShakeProbability(totalProbability);
            for (int k = 0; k < ShakeChecks; k++)
                if (!(rng.NextDouble() < each)) return new CatchResult(false, k, totalProbability);
            return new CatchResult(true, ShakeChecks, totalProbability);
        }

        public static CatchResult Attempt(GameData data, Monster target, IRng rng,
                                          double ballMultiplier = 1.0, double statusMultiplier = 1.0, double throwQuality = 1.0)
        {
            double p = Probability(target.Species(data).CatchRate, target.Hp, target.MaxHp,
                                   ballMultiplier * Math.Max(0.0, throwQuality), statusMultiplier);
            return RollShakes(p, rng);
        }
    }
}
