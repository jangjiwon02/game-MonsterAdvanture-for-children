using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using MonsterAdventure.Net;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    /// <summary>LAN 접속자 색 배정 + 일반 게임 저장을 그대로 들고 LAN에 들어오는 흐름.</summary>
    public class PlayerLookTests
    {
        [Test]
        public void FirstPlayer_GetsDefaultRed()
        {
            Assert.AreEqual(PlayerLook.DefaultColor, PlayerLook.PickColor(new int[0], new SystemRng(1)));
        }

        [Test]
        public void NewPlayers_NeverShareAColorWhileFreeOnesRemain()
        {
            for (int seed = 0; seed < 50; seed++)
            {
                var rng = new SystemRng(seed);
                var used = new System.Collections.Generic.List<int>();
                for (int i = 0; i < PlayerLook.ColorCount; i++) used.Add(PlayerLook.PickColor(used, rng));
                Assert.AreEqual(PlayerLook.ColorCount, used.Distinct().Count(), $"seed {seed}: {string.Join(",", used)}");
                Assert.AreEqual(PlayerLook.DefaultColor, used[0]);
            }
        }

        [Test]
        public void ReusesAFreedColor_AndOverflowsToLeastUsed()
        {
            var rng = new SystemRng(3);
            // 0 이 비어 있으면(첫 사람이 나간 뒤) 그 자리가 남은 유일한 후보가 아니어도 이미 쓰는 색은 안 고른다.
            var used = Enumerable.Range(1, PlayerLook.ColorCount - 1).ToList();
            Assert.AreEqual(0, PlayerLook.PickColor(used, rng));
            // 팔레트가 다 차면 가장 덜 쓰인 색을 고른다(0 만 두 번 쓰이는 경우 0 은 제외).
            var full = Enumerable.Range(0, PlayerLook.ColorCount).Concat(new[] { 0 }).ToList();
            Assert.AreNotEqual(0, PlayerLook.PickColor(full, rng));
        }

        [Test]
        public void ArenaState_AssignsDistinctColorsToJoiners()
        {
            var data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));
            var a = new ArenaState(data, WorldMap.Generate(), new SystemRng(7));
            var t1 = a.Join("하나", 0);
            var t2 = a.Join("둘", 0);
            var t3 = a.Join("셋", 0);
            Assert.AreEqual(PlayerLook.DefaultColor, t1.Color);
            Assert.AreEqual(3, new[] { t1.Color, t2.Color, t3.Color }.Distinct().Count());
            Assert.AreEqual(t2.Color, a.Get(t2.Id).ToInfo().Color);
        }
    }

    public class LanUnifiedProgressIntegrationTests
    {
        GameData _data; WorldMap _map;

        [OneTimeSetUp]
        public void Load()
        {
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));
            _map = WorldMap.Generate();
        }

        static (string type, JObject data) WaitFor(TcpArenaClient c, string type, int timeoutMs = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (c.TryTakeReceived(out var t, out var d) && t == type) return (t, d);
                System.Threading.Thread.Sleep(5);
            }
            Assert.Fail($"{timeoutMs}ms 안에 '{type}' 메시지를 받지 못했다");
            return default;
        }

        [Test]
        public void HelloWithSave_KeepsPartyAndLevels_AndSecondJoinerGetsAnotherColor()
        {
            var save = PlayerState.NewGame(_data, 0);
            save.PlayerName = "민수";
            save.Party[0] = Monster.Create(_data, 0, 23);   // 일반 게임에서 혼자 키운 불꼬마 Lv23
            save.Money = 4321;

            using var server = new TcpArenaServer(_data, _map);   // 계정 저장소 없음 — 클라이언트가 보낸 저장만 쓴다
            server.Start();

            using var c1 = new TcpArenaClient();
            c1.Connect("127.0.0.1", server.Port, "민수", 0, 4000, SaveSerializer.ToJson(save));
            var (_, w1) = WaitFor(c1, NetMsgType.Welcome);
            var back = SaveSerializer.FromJson(_data, (string)w1["StateJson"]);
            Assert.IsNotNull(back);
            Assert.AreEqual(23, back.Party[0].Level, "일반 게임의 레벨이 그대로여야 한다");
            Assert.GreaterOrEqual(back.Money, 4321, "소지금(출석 보너스가 더해졌을 수 있음)");
            Assert.AreEqual("민수", back.PlayerName);
            Assert.AreEqual(PlayerLook.DefaultColor, (int)w1["Color"]);

            using var c2 = new TcpArenaClient();
            c2.Connect("127.0.0.1", server.Port, "영희", 2);
            var (_, w2) = WaitFor(c2, NetMsgType.Welcome);
            Assert.AreNotEqual((int)w1["Color"], (int)w2["Color"]);
            var (_, joined) = WaitFor(c1, NetMsgType.Joined);
            Assert.AreEqual((int)w2["Color"], (int)joined["Info"]["Color"]);
        }

        [Test]
        public void Connect_RetriesUntilTheServerAnswers_InsteadOfFailingOnTheFirstTry()
        {
            // 첫 시도가 실패하는 상황(폰 Wi-Fi 의 첫 패킷 유실 등)을, 서버가 조금 늦게 열리는 것으로 흉내 낸다.
            int port;
            { var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
            TcpArenaServer server = null;
            var opener = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(600);
                server = new TcpArenaServer(_data, _map, null, port);
                server.Start();
            });
            opener.Start();
            try
            {
                using var c = new TcpArenaClient();
                c.Connect("127.0.0.1", port, "재시도", 0, 1000, null, 8);
                WaitFor(c, NetMsgType.Welcome);
            }
            finally { opener.Join(); server?.Dispose(); }
        }

        [Test]
        public void Connect_GivesUpWithAnException_WhenNobodyListens()
        {
            int port;
            { var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
            using var c = new TcpArenaClient();
            Assert.Catch(() => c.Connect("127.0.0.1", port, "아무도없음", 0, 300, null, 2));
        }
    }
}
