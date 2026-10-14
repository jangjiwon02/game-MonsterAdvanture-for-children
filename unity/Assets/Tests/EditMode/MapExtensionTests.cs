using System.Collections.Generic;
using System.IO;
using MonsterAdventure.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    /// <summary>북쪽으로 늘린 지도(연수동·연수주공아파트)와, 늘리기 전에 저장한 위치의 이전.</summary>
    public class MapExtensionTests
    {
        GameData _data;
        WorldMap _map;

        [OneTimeSetUp]
        public void Load()
        {
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));
            _map = WorldMap.Generate();
            WorldMap.AddChungjuLandmarks(_map);
        }

        HashSet<(int, int)> ReachableFromStart()
        {
            var seen = new HashSet<(int, int)>();
            var q = new Queue<(int, int)>();
            var start = (WorldMap.VillageX, WorldMap.VillageY + 1);
            q.Enqueue(start); seen.Add(start);
            while (q.Count > 0)
            {
                var (x, y) = q.Dequeue();
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var n = (x + dx, y + dy);
                    if (seen.Contains(n) || !_map.IsPassable(n.Item1, n.Item2)) continue;
                    seen.Add(n); q.Enqueue(n);
                }
            }
            return seen;
        }

        [Test]
        public void Map_IsTallerByTheNorthExtension_AndVillageMovedDown()
        {
            Assert.AreEqual(WorldMap.GenHeight + WorldMap.NorthExtension, WorldMap.Height);
            Assert.AreEqual(WorldMap.GenVillageY + WorldMap.NorthExtension, WorldMap.VillageY);
            Assert.AreEqual(Tile.CenterDoor, _map[WorldMap.VillageX - 3, WorldMap.VillageY - 3]);
        }

        [Test]
        public void YeonsuApartments_AreBuiltAndBlockWalking()
        {
            Assert.AreEqual(6, WorldMap.YeonsuApartments.Length);
            foreach (var (ax, ay, _, _) in WorldMap.YeonsuApartments)
            {
                Assert.AreEqual(Tile.AptRoof, _map[ax + 1, ay]);
                Assert.AreEqual(Tile.AptWall, _map[ax, ay + 2]);   // 가운데 아랫칸은 문일 수 있다
                Assert.IsFalse(_map.IsPassable(ax + 1, ay + 1), "아파트는 장식이라 들어갈 수 없다");
                Assert.IsTrue(WorldMap.InYeonsu(ax + 1, ay + 1));
            }
            Assert.AreEqual("연수동", WorldMap.DisplayAreaName(WorldMap.YeonsuSignTileX + 3, WorldMap.YeonsuSignTileY));
        }

        [Test]
        public void SekyungApartment_StandsClockwise270DegreesFromTheCenterToNamsanDirection()
        {
            // 센터(아동센터 건물 중심) → 남산초등학교 방향의 방위각(북 0°, 시계 방향)에 270°를 더한 방향이어야 한다.
            double Bearing(double dx, double dy) => (System.Math.Atan2(dx, -dy) * 180 / System.Math.PI + 360) % 360;
            double cx = WorldMap.VillageX - 3, cy = WorldMap.VillageY - 3.5;
            double toNamsan = Bearing(WorldMap.NamsanTileX - cx, WorldMap.NamsanTileY - 0.5 - cy);
            double expected = (toNamsan + 270) % 360;
            double toSekyung = Bearing(WorldMap.SekyungX + 1 - cx, WorldMap.SekyungY + 1 - cy);
            double diff = System.Math.Abs(((toSekyung - expected + 540) % 360) - 180);
            Assert.Less(diff, 10, $"기대 방위 {expected:F0}° 인데 세경아파트는 {toSekyung:F0}°");

            Assert.AreEqual(Tile.AptRoof, _map[WorldMap.SekyungX + 1, WorldMap.SekyungY]);
            Assert.AreEqual(Tile.AptWall, _map[WorldMap.SekyungX, WorldMap.SekyungY + 2]);
            Assert.IsFalse(_map.IsPassable(WorldMap.SekyungX + 1, WorldMap.SekyungY + 1));
            // 닿을 수 있어야 한다: 블록 바로 아래 줄이 걸을 수 있다.
            var reach = ReachableFromStart();
            Assert.IsTrue(reach.Contains((WorldMap.SekyungX + 1, WorldMap.SekyungY + 3)));
        }

        [Test]
        public void Yeonsu_IsReachableOnFootFromTheVillage_AndMostOfTheMapIsConnected()
        {
            var reach = ReachableFromStart();
            foreach (var (ax, ay, _, _) in WorldMap.YeonsuApartments)
            {
                // 아파트 블록 바로 앞(아래쪽) 칸과 왼쪽 칸 중 하나는 걸어서 닿아야 한다.
                bool ok = false;
                for (int x = ax - 1; x <= ax + 3 && !ok; x++) ok = reach.Contains((x, ay + 3));
                for (int y = ay; y <= ay + 2 && !ok; y++) ok = reach.Contains((ax - 1, y)) || reach.Contains((ax + 3, y));
                Assert.IsTrue(ok, $"연수주공 블록({ax},{ay}) 주변에 걸어서 갈 수 없다");
            }
            Assert.IsTrue(reach.Contains((WorldMap.YeonsuSignTileX, WorldMap.YeonsuSignTileY)));

            int passable = 0;
            for (int y = 0; y < WorldMap.Height; y++)
                for (int x = 0; x < WorldMap.Width; x++)
                    if (_map.IsPassable(x, y)) passable++;
            Assert.Greater(reach.Count, passable * 0.95, $"걸을 수 있는 {passable}칸 중 닿는 곳 {reach.Count}칸 — 막힌 구석이 너무 많다");
        }

        [Test]
        public void NorthernRoadsConnectToTheOldMap()
        {
            // 웹 지형 맨 윗줄(옛 테두리)이 터져서 북쪽과 이어져야 한다.
            int open = 0;
            for (int x = 1; x < WorldMap.Width - 1; x++)
                if (_map.IsPassable(x, WorldMap.NorthExtension)) open++;
            Assert.Greater(open, 30);
        }

        [Test]
        public void OldSave_WithoutMapRevision_MovesDownSoThePlayerStaysOnTheSameGround()
        {
            var s = PlayerState.NewGame(_data, 0);
            s.X = 20; s.Y = 19;   // 예전(북쪽 확장 전) 좌표
            var env = JObject.Parse(SaveSerializer.ToJson(s));
            ((JObject)env["State"]).Remove("MapRevision");   // 예전 저장 파일에는 이 항목이 없다
            var loaded = SaveSerializer.FromJson(_data, env.ToString());
            Assert.AreEqual((20, 19 + WorldMap.NorthExtension), (loaded.X, loaded.Y));

            // 새 저장은 다시 옮기지 않는다.
            var again = SaveSerializer.FromJson(_data, SaveSerializer.ToJson(loaded));
            Assert.AreEqual((20, 19 + WorldMap.NorthExtension), (again.X, again.Y));
        }
    }
}
