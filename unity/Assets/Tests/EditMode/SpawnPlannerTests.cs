using System;
using System.Collections.Generic;
using System.Linq;
using MonsterAdventure.Core;
using NUnit.Framework;

namespace MonsterAdventure.Tests
{
    /// <summary>오버월드 스폰 계획(SpawnPlanner) 검증. 실제 충주 맵 위에서 돌린다.</summary>
    public class SpawnPlannerTests
    {
        WorldMap _map;
        SpawnConfig _cfg;
        (int X, int Y) _grassPlayer;      // 주변에 풀숲이 넉넉한 칸

        [SetUp]
        public void SetUp()
        {
            _map = WorldMap.Generate();
            WorldMap.AddChungjuLandmarks(_map);
            _cfg = new SpawnConfig();
            int best = -1;
            for (int y = 2; y < WorldMap.Height - 2; y++)
                for (int x = 2; x < WorldMap.Width - 2; x++)
                {
                    if (!_map.IsPassable(x, y)) continue;
                    int n = SpawnPlanner.Candidates(_map, x, y, new List<(int X, int Y)>(), _cfg).Count;
                    if (n > best) { best = n; _grassPlayer = (x, y); }
                }
            Assert.Greater(best, 30, "테스트용 풀숲 밀집 지점이 있어야 한다");
        }

        /// <summary>계획을 반복 적용해 활성 목록을 채운다(런타임이 하는 일과 같다).</summary>
        List<SpawnPoint> Fill(int px, int py, IRng rng, int ticks, List<SpawnPoint> start = null)
        {
            var active = start ?? new List<SpawnPoint>();
            int id = active.Count == 0 ? 1 : active.Max(a => a.Id) + 1;
            for (int t = 0; t < ticks; t++)
            {
                var plan = SpawnPlanner.Plan(_map, px, py, active, _cfg, rng);
                active.RemoveAll(a => plan.DespawnIds.Contains(a.Id));
                foreach (var s in plan.Spawns) active.Add(new SpawnPoint(id++, s.X, s.Y));
            }
            return active;
        }

        [Test]
        public void Spawns_OnlyOnTallGrass_WithinRadius_AndOutsideMinDistance()
        {
            var (px, py) = _grassPlayer;
            for (int seed = 1; seed <= 20; seed++)
            {
                var active = Fill(px, py, new Mulberry32(seed), 12);
                Assert.IsNotEmpty(active);
                foreach (var a in active)
                {
                    Assert.AreEqual(Tile.TallGrass, _map[a.X, a.Y]);
                    double d = Math.Sqrt((a.X - px) * (a.X - px) + (a.Y - py) * (a.Y - py));
                    Assert.LessOrEqual(d, _cfg.SpawnRadius + 1e-9);
                    Assert.GreaterOrEqual(d, _cfg.MinPlayerDistance - 1e-9);
                }
            }
        }

        [Test]
        public void NeverSpawnsOnPlayerTile()
        {
            var (px, py) = _grassPlayer;
            for (int seed = 1; seed <= 30; seed++)
                Assert.False(Fill(px, py, new Mulberry32(seed), 10).Any(a => a.X == px && a.Y == py));
        }

        [Test]
        public void RespectsMaxActive_AndFillsUpToIt()
        {
            var (px, py) = _grassPlayer;
            var active = new List<SpawnPoint>();
            var rng = new Mulberry32(7);
            for (int t = 0; t < 30; t++)
            {
                Fill(px, py, rng, 1, active);
                Assert.LessOrEqual(active.Count, _cfg.MaxActive);
            }
            Assert.AreEqual(_cfg.MaxActive, active.Count);
        }

        [Test]
        public void MaxSpawnsPerTick_LimitsNewSpawns()
        {
            var (px, py) = _grassPlayer;
            _cfg.MaxSpawnsPerTick = 2;
            var plan = SpawnPlanner.Plan(_map, px, py, new List<SpawnPoint>(), _cfg, new Mulberry32(3));
            Assert.AreEqual(2, plan.Spawns.Count);
        }

        [Test]
        public void SpawnsNeverOverlapOrTouch()
        {
            var (px, py) = _grassPlayer;
            for (int seed = 1; seed <= 20; seed++)
            {
                var a = Fill(px, py, new Mulberry32(seed), 15);
                for (int i = 0; i < a.Count; i++)
                    for (int j = i + 1; j < a.Count; j++)
                        Assert.GreaterOrEqual(Math.Max(Math.Abs(a[i].X - a[j].X), Math.Abs(a[i].Y - a[j].Y)), _cfg.MinSpacing);
            }
        }

        [Test]
        public void NoTallGrassNearby_NoSpawns()
        {
            // 마을 한복판: 풀숲은 마을 중심에서 7칸 밖에만 생긴다.
            _cfg.SpawnRadius = 5;
            var plan = SpawnPlanner.Plan(_map, WorldMap.VillageX, WorldMap.VillageY, new List<SpawnPoint>(), _cfg, new Mulberry32(1));
            Assert.IsEmpty(plan.Spawns);
        }

        [Test]
        public void FarSpawns_AreDespawned_ButNearAndLockedOnesStay()
        {
            var (px, py) = _grassPlayer;
            int far = (int)Math.Ceiling(_cfg.DespawnRadius) + 1;
            int farX = px + far < WorldMap.Width ? px + far : px - far;
            var active = new List<SpawnPoint>
            {
                new SpawnPoint(1, farX, py),                    // 디스폰 반경 밖
                new SpawnPoint(2, farX, py + 2, locked: true),  // 밖이지만 접촉 중
                new SpawnPoint(3, px + 1, py),                  // 안
            };
            var plan = SpawnPlanner.Plan(_map, px, py, active, _cfg, new Mulberry32(1));
            CollectionAssert.AreEqual(new[] { 1 }, plan.DespawnIds);
        }

        [Test]
        public void BetweenSpawnRadiusAndMargin_IsNotDespawned()
        {
            var (px, py) = _grassPlayer;
            int x = px + (int)_cfg.SpawnRadius + 2;            // 스폰 반경 밖이지만 디스폰 반경 안
            if (x >= WorldMap.Width) x = px - (int)_cfg.SpawnRadius - 2;
            var plan = SpawnPlanner.Plan(_map, px, py, new List<SpawnPoint> { new SpawnPoint(1, x, py) }, _cfg, new Mulberry32(1));
            Assert.IsEmpty(plan.DespawnIds);
        }

        [Test]
        public void PlayerMovesFarAway_AllOldSpawnsAreDespawned()
        {
            var (px, py) = _grassPlayer;
            var active = Fill(px, py, new Mulberry32(5), 20);
            Assert.AreEqual(_cfg.MaxActive, active.Count);
            int qx = px < WorldMap.Width / 2 ? WorldMap.Width - 3 : 2;
            var plan = SpawnPlanner.Plan(_map, qx, py, active, _cfg, new Mulberry32(5));
            Assert.AreEqual(active.Count, plan.DespawnIds.Count);
        }

        [Test]
        public void SameSeed_SamePlan_DifferentSeed_Differs()
        {
            var (px, py) = _grassPlayer;
            var a = Fill(px, py, new Mulberry32(42), 10).Select(s => (s.X, s.Y)).ToList();
            var b = Fill(px, py, new Mulberry32(42), 10).Select(s => (s.X, s.Y)).ToList();
            var c = Fill(px, py, new Mulberry32(43), 10).Select(s => (s.X, s.Y)).ToList();
            CollectionAssert.AreEqual(a, b);
            CollectionAssert.AreNotEqual(a, c);
        }

        [Test]
        public void Wander_StaysOnTallGrass_AndAvoidsPlayerAndOthers()
        {
            var (px, py) = _grassPlayer;
            var cands = SpawnPlanner.Candidates(_map, px, py, new List<(int X, int Y)>(), _cfg);
            var rng = new Mulberry32(9);
            int moved = 0;
            foreach (var (x, y) in cands.Take(60))
            {
                var other = new List<(int X, int Y)> { (x + 1, y) };
                if (!SpawnPlanner.TryPickWander(_map, x, y, px, py, other, rng, out var t)) continue;
                moved++;
                Assert.AreEqual(1, Math.Abs(t.X - x) + Math.Abs(t.Y - y));
                Assert.AreEqual(Tile.TallGrass, _map[t.X, t.Y]);
                Assert.False(t.X == px && t.Y == py);
                Assert.False(other.Contains(t));
            }
            Assert.Greater(moved, 10);
        }

        [Test]
        public void Wander_WhenBoxedIn_ReturnsFalse()
        {
            var (px, py) = _grassPlayer;
            var (x, y) = SpawnPlanner.Candidates(_map, px, py, new List<(int X, int Y)>(), _cfg)[0];
            var block = new List<(int X, int Y)> { (x, y - 1), (x + 1, y), (x, y + 1), (x - 1, y) };
            Assert.False(SpawnPlanner.TryPickWander(_map, x, y, px, py, block, new Mulberry32(1), out _));
        }

        [Test]
        public void IsAdjacent_UsesChebyshevDistance()
        {
            Assert.IsTrue(SpawnPlanner.IsAdjacent(5, 5, 6, 6));
            Assert.IsTrue(SpawnPlanner.IsAdjacent(5, 5, 5, 4));
            Assert.IsTrue(SpawnPlanner.IsAdjacent(5, 5, 5, 5));
            Assert.IsFalse(SpawnPlanner.IsAdjacent(5, 5, 7, 5));
            Assert.IsFalse(SpawnPlanner.IsAdjacent(5, 5, 7, 6));
        }
    }
}
