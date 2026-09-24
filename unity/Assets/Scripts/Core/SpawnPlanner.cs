using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    /// <summary>오버월드 스폰 설정. 거리는 모두 타일 단위이고 맵 좌표(y 아래로 증가)를 쓴다.</summary>
    public sealed class SpawnConfig
    {
        /// <summary>플레이어 중심 이 반경(유클리드) 안에서만 새로 나타난다.</summary>
        public double SpawnRadius = 8;
        /// <summary>플레이어에게 이보다 가깝게는 나타나지 않는다(눈앞에서 툭 튀어나오지 않게).</summary>
        public double MinPlayerDistance = 3;
        public int MaxActive = 5;
        /// <summary>스폰 반경 밖 여유. 경계에서 나타났다 사라졌다 반복하지 않게 디스폰 반경을 더 크게 둔다.</summary>
        public double DespawnMargin = 4;
        /// <summary>다른 스폰과의 최소 체비셰프 간격. 2 = 서로 인접하지 않음, 1 = 같은 칸만 금지.</summary>
        public int MinSpacing = 2;
        /// <summary>한 번 평가에 새로 만드는 최대 수(한꺼번에 우르르 나타나지 않게).</summary>
        public int MaxSpawnsPerTick = 1;
        /// <summary>런타임이 SpawnPlanner 를 평가하는 주기(초).</summary>
        public float TickSeconds = 0.5f;
        /// <summary>승리·포획으로 몬스터가 사라진 뒤 다음 스폰까지 쉬는 시간(초). 런타임이 쓴다.</summary>
        public float RespawnDelaySeconds = 3f;

        public double DespawnRadius => SpawnRadius + DespawnMargin;
    }

    /// <summary>이미 나와 있는 스폰 하나. Locked 면 디스폰하지 않는다(접촉·전투 중).</summary>
    public readonly struct SpawnPoint
    {
        public readonly int Id, X, Y;
        public readonly bool Locked;
        public SpawnPoint(int id, int x, int y, bool locked = false) { Id = id; X = x; Y = y; Locked = locked; }
    }

    /// <summary>한 번 평가한 결과.</summary>
    public sealed class SpawnPlan
    {
        public readonly List<(int X, int Y)> Spawns = new List<(int X, int Y)>();
        public readonly List<int> DespawnIds = new List<int>();
    }

    /// <summary>
    /// 플레이어 주변 풀숲에 야생 몬스터를 몇 마리 유지할지 정하는 순수 로직(엔진 무관, 난수원만 받으므로 결정적).
    /// 어떤 몬스터가 될지는 여기서 정하지 않는다 — 런타임이 위치가 정해진 뒤 WildEncounter.Generate 로 정한다.
    /// </summary>
    public static class SpawnPlanner
    {
        public static SpawnPlan Plan(WorldMap map, int playerX, int playerY,
            IReadOnlyList<SpawnPoint> active, SpawnConfig config, IRng rng)
        {
            var plan = new SpawnPlan();
            var kept = new List<(int X, int Y)>();

            double despawnSq = config.DespawnRadius * config.DespawnRadius;
            foreach (var a in active)
            {
                if (!a.Locked && DistSq(a.X, a.Y, playerX, playerY) > despawnSq) plan.DespawnIds.Add(a.Id);
                else kept.Add((a.X, a.Y));
            }

            int slots = Math.Min(config.MaxSpawnsPerTick, config.MaxActive - kept.Count);
            for (int n = 0; n < slots; n++)
            {
                var candidates = Candidates(map, playerX, playerY, kept, config);
                if (candidates.Count == 0) break;
                var pick = rng.Pick(candidates);
                plan.Spawns.Add(pick);
                kept.Add(pick);
            }
            return plan;
        }

        /// <summary>새 스폰이 될 수 있는 칸(y, x 순서로 나열 — 같은 입력이면 같은 순서).</summary>
        public static List<(int X, int Y)> Candidates(WorldMap map, int playerX, int playerY,
            IReadOnlyList<(int X, int Y)> occupied, SpawnConfig config)
        {
            var list = new List<(int X, int Y)>();
            int r = (int)Math.Ceiling(config.SpawnRadius);
            double maxSq = config.SpawnRadius * config.SpawnRadius, minSq = config.MinPlayerDistance * config.MinPlayerDistance;
            for (int y = playerY - r; y <= playerY + r; y++)
                for (int x = playerX - r; x <= playerX + r; x++)
                {
                    if (!WorldMap.InBounds(x, y) || map[x, y] != Tile.TallGrass) continue;
                    double d = DistSq(x, y, playerX, playerY);
                    if (d > maxSq || d < minSq) continue;
                    bool blocked = false;
                    foreach (var o in occupied)
                        if (Math.Max(Math.Abs(o.X - x), Math.Abs(o.Y - y)) < config.MinSpacing) { blocked = true; break; }
                    if (!blocked) list.Add((x, y));
                }
            return list;
        }

        /// <summary>배회할 다음 칸(상하좌우 한 칸): 풀숲 안에서만, 플레이어·다른 몬스터가 있는 칸은 제외. 갈 곳이 없으면 false.</summary>
        public static bool TryPickWander(WorldMap map, int x, int y, int playerX, int playerY,
            IReadOnlyList<(int X, int Y)> occupied, IRng rng, out (int X, int Y) target)
        {
            var options = new List<(int X, int Y)>(4);
            foreach (var (dx, dy) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
            {
                int nx = x + dx, ny = y + dy;
                if (!WorldMap.InBounds(nx, ny) || map[nx, ny] != Tile.TallGrass || !map.IsPassable(nx, ny)) continue;
                if (nx == playerX && ny == playerY) continue;
                bool taken = false;
                foreach (var o in occupied) if (o.X == nx && o.Y == ny) { taken = true; break; }
                if (!taken) options.Add((nx, ny));
            }
            if (options.Count == 0) { target = (x, y); return false; }
            target = rng.Pick(options);
            return true;
        }

        /// <summary>두 칸이 서로 인접(체비셰프 거리 1 이내, 같은 칸 포함)한지. 접촉 판정에 쓴다.</summary>
        public static bool IsAdjacent(int ax, int ay, int bx, int by) => Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by)) <= 1;

        static double DistSq(int ax, int ay, int bx, int by)
        {
            double dx = ax - bx, dy = ay - by;
            return dx * dx + dy * dy;
        }
    }
}
