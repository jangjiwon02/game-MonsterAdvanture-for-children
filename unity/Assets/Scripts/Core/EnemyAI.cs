using System.Collections.Generic;
using System.Linq;

namespace MonsterAdventure.Core
{
    /// <summary>SPEC "적 AI": 기술 점수 = power * typeMult * (STAB ? 1.3 : 1) * accuracy/100, 60%는 최고 점수·40%는 무작위.</summary>
    public static class EnemyAI
    {
        public const double BestMoveChance = 0.6;

        public static string PickMove(GameData data, Monster enemy, Monster target, IRng rng)
        {
            string enemyType = enemy.Species(data).Type;
            string targetType = target.Species(data).Type;
            var scored = enemy.Moves
                .Select(id =>
                {
                    var mv = data.GetMove(id);
                    double score = mv.Power * data.TypeMultiplier(mv.Type, targetType)
                                   * (enemyType == mv.Type ? 1.3 : 1.0) * mv.Accuracy / 100.0;
                    return (id, score);
                })
                .OrderByDescending(s => s.score)   // 안정 정렬: 동점이면 배우는 순서대로
                .ToList();
            return rng.NextDouble() < BestMoveChance ? scored[0].id : rng.Pick(scored).id;
        }

        /// <summary>기술을 쓸 때 플레이어가 먼저인지. 스피드 동률이면 무작위.</summary>
        public static bool PlayerMovesFirst(int playerSpeed, int enemySpeed, IRng rng) =>
            playerSpeed > enemySpeed || (playerSpeed == enemySpeed && rng.NextDouble() < 0.5);
    }
}
