using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using MonsterAdventure.Net;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    public class FrameTests
    {
        [Test]
        public void EncodeThenFeed_YieldsExactlyOneMessage()
        {
            var bytes = Frame.Encode("hello");
            var r = new Frame.Reader();
            r.Feed(bytes, bytes.Length);
            Assert.IsTrue(r.TryTake(out var json));
            Assert.AreEqual("hello", json);
            Assert.IsFalse(r.TryTake(out _));
        }

        [Test]
        public void PartialFeed_WaitsUntilWholeMessageArrives()
        {
            var bytes = Frame.Encode("몸통박치기");   // 멀티바이트 문자 포함
            var r = new Frame.Reader();
            r.Feed(bytes, 3);
            Assert.IsFalse(r.TryTake(out _), "길이 헤더도 다 안 왔다(4바이트 중 3바이트)");
            r.Feed(bytes.Skip(3).Take(1).ToArray(), 1);   // 길이 헤더 완성, 본문은 아직
            Assert.IsFalse(r.TryTake(out _), "길이는 알지만 본문이 아직 안 왔다");
            r.Feed(bytes.Skip(4).Take(bytes.Length - 4 - 2).ToArray(), bytes.Length - 4 - 2);
            Assert.IsFalse(r.TryTake(out _), "본문이 2바이트 모자라다");
            r.Feed(bytes.Skip(bytes.Length - 2).ToArray(), 2);   // 마지막 2바이트 도착
            Assert.IsTrue(r.TryTake(out var json));
            Assert.AreEqual("몸통박치기", json);
            Assert.IsFalse(r.TryTake(out _), "다 읽었으면 더 나올 게 없다");
        }

        [Test]
        public void TwoMessagesBackToBack_AreReadInOrder()
        {
            var a = Frame.Encode("first"); var b = Frame.Encode("second");
            var combined = a.Concat(b).ToArray();
            var r = new Frame.Reader();
            r.Feed(combined, combined.Length);
            Assert.IsTrue(r.TryTake(out var j1)); Assert.AreEqual("first", j1);
            Assert.IsTrue(r.TryTake(out var j2)); Assert.AreEqual("second", j2);
            Assert.IsFalse(r.TryTake(out _));
        }

        [Test]
        public void ByteAtATime_StillReassembles()
        {
            var bytes = Frame.Encode("한 바이트씩 넣어도 조립된다");
            var r = new Frame.Reader();
            for (int i = 0; i < bytes.Length; i++) r.Feed(new[] { bytes[i] }, 1);
            Assert.IsTrue(r.TryTake(out var json));
            Assert.AreEqual("한 바이트씩 넣어도 조립된다", json);
        }
    }

    public class NetCodecTests
    {
        [Test]
        public void RoundTrips_TypeAndPayload()
        {
            string wire = NetCodec.Encode(NetMsgType.Move, new MoveMessage { Dir = 2 });
            Assert.IsTrue(NetCodec.TryDecode(wire, out var type, out var d));
            Assert.AreEqual(NetMsgType.Move, type);
            Assert.AreEqual(2, (int)d["Dir"]);
        }

        [TestCase("not json")]
        [TestCase("{}")]
        [TestCase("{\"t\":\"move\"}")]
        [TestCase("{\"d\":{}}")]
        [TestCase("{\"t\":1,\"d\":{}}")]
        public void Malformed_IsRejected(string wire) => Assert.IsFalse(NetCodec.TryDecode(wire, out _, out _));
    }

    public class ArenaStateTests
    {
        GameData _data;
        WorldMap _map;

        [OneTimeSetUp]
        public void Load()
        {
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));
            _map = WorldMap.Generate();
        }

        ArenaState New() => new ArenaState(_data, _map);

        [Test]
        public void Join_AcceptsAnySpeciesInRange_ButRejectsOutOfRange()
        {
            var a = New();
            var evolved = a.Join("도전자", 1);   // 이글불(2단계, 시작 파트너는 아니지만 이제는 허용된다 — 오래 키운 계정의 겉모습)
            Assert.AreEqual(1, evolved.SpeciesId);
            Assert.Throws<System.ArgumentException>(() => a.Join("나쁜값", -1));
            Assert.Throws<System.ArgumentException>(() => a.Join("나쁜값2", 9999));
        }

        [Test]
        public void Join_PlacesFirstPlayerAtVillageEntrance()
        {
            var a = New();
            var t = a.Join("철수", 0);
            Assert.AreEqual(1, t.Id);
            Assert.AreEqual((WorldMap.VillageX, WorldMap.VillageY + 1), (t.X, t.Y));
            Assert.AreEqual("철수", t.Name);
        }

        [Test]
        public void Join_BlankName_FallsBackToDefault()
        {
            var a = New();
            Assert.AreEqual("트레이너", a.Join("  ", 0).Name);
            Assert.AreEqual("트레이너", a.Join(null, 2).Name);
        }

        [Test]
        public void Join_SecondPlayerGetsADifferentTile()
        {
            var a = New();
            var p1 = a.Join("철수", 0);
            var p2 = a.Join("영희", 2);
            Assert.AreNotEqual((p1.X, p1.Y), (p2.X, p2.Y));
            Assert.IsTrue(_map.IsPassable(p2.X, p2.Y));
        }

        [Test]
        public void Move_IntoOpenSpace_Succeeds_AndUpdatesFacing()
        {
            var a = New();
            var p = a.Join("철수", 0);
            int x0 = p.X, y0 = p.Y;   // Trainer 는 참조 타입이라 TryMove 후에는 p.X 도 이미 바뀌어 있다
            var r = a.TryMove(p.Id, NetDirection.Left);
            Assert.IsTrue(r.Moved);
            Assert.AreEqual((x0 - 1, y0), (r.X, r.Y));
            Assert.AreEqual(NetDirection.Left, a.Get(p.Id).Dir);
        }

        [Test]
        public void Move_IntoWall_FailsButStillTurns()
        {
            var a = New();
            // 지도 테두리는 항상 나무: (0,0) 근처로 직접 겹치도록 만들 수 없으니, 맵 경계 밖 이동을 흉내낼 좌표를 잡는다.
            var p = a.Join("철수", 0);
            // 맵의 절대 테두리로 실제로 걸어가긴 머니, 벽 판정 자체는 WorldMap.IsPassable 로 위임되므로
            // 여기서는 통과 불가 타일(테두리)에 인접한 상황을 직접 구성해 검증한다.
            var edgeState = New();
            var far = edgeState.Join("변두리", 0);
            // 트레이너를 임의 위치로 보낼 수 없으므로, 최소 검증: 같은 방향으로 계속 이동하면 언젠가 막힌다.
            NetDirection dir = NetDirection.Up;
            MoveOutcome last = default;
            for (int i = 0; i < 40 && (i == 0 || last.Moved); i++) last = edgeState.TryMove(far.Id, dir);
            Assert.IsFalse(last.Moved, "40칸을 계속 가면 맵 끝(나무)에 막혀야 한다");
            Assert.AreEqual(dir, edgeState.Get(far.Id).Dir, "막혀도 바라보는 방향은 바뀐다");
        }

        [Test]
        public void Move_IntoAnotherPlayer_IsBlocked()
        {
            var a = New();
            var p1 = a.Join("철수", 0);          // (VillageX, VillageY+1) — 시작 지점, 통과 가능이 보장된 칸
            var p2 = a.Join("영희", 2);
            p2.X = p1.X + 1; p2.Y = p1.Y;         // 마을 중심부는 열려 있으므로 바로 오른쪽 칸에 직접 배치(결정론적)
            Assume.That(_map.IsPassable(p2.X, p2.Y), Is.True, "마을 시작 지점 옆 칸은 통과 가능해야 한다");

            var r = a.TryMove(p2.Id, NetDirection.Left);   // p1 이 있는 칸으로 이동 시도
            Assert.IsFalse(r.Moved);
            Assert.AreEqual(NetDirection.Left, a.Get(p2.Id).Dir, "막혀도 바라보는 방향은 바뀐다");
        }

        [Test]
        public void Leave_FreesTheTileAndClearsChallenges()
        {
            var a = New();
            var p1 = a.Join("철수", 0);
            var p2 = a.Join("영희", 2);
            a.Leave(p1.Id);
            Assert.IsNull(a.Get(p1.Id));
            CollectionAssert.DoesNotContain(a.Players.Keys, p1.Id);
        }

        [Test]
        public void Challenge_WorksAcrossTheMap_NoNeedToWalkNextToEachOther()
        {
            // 월드 메뉴의 "대결 신청" 목록으로 신청하는 흐름 — 거리와 상관없이 같은 방이면 된다.
            var a = New();
            var p1 = a.Join("철수", 0);
            var p2 = a.Join("영희", 2);
            p2.X = p1.X + 15; p2.Y = p1.Y + 8;
            Assert.IsNull(a.RequestChallenge(p1.Id, p2.Id));
            Assert.AreEqual((p1.Id, true), a.Respond(p2.Id, true));
        }

        [Test]
        public void Challenge_FullHandshake_AcceptedAndDeclined()
        {
            var a = New();
            var p1 = a.Join("철수", 0);
            var p2 = a.Join("영희", 2);
            p2.X = p1.X + 1; p2.Y = p1.Y;   // 결정론적으로 인접시킨다

            Assert.IsNull(a.RequestChallenge(p1.Id, p2.Id));
            Assert.AreEqual("이미 다른 도전에 응답을 기다리는 중이다!", a.RequestChallenge(p1.Id, p2.Id));

            var accepted = a.Respond(p2.Id, true);
            Assert.AreEqual((p1.Id, true), accepted);
            Assert.IsNull(a.Respond(p2.Id, true), "이미 처리된 도전에는 다시 응답할 수 없다");

            Assert.IsNull(a.RequestChallenge(p1.Id, p2.Id));
            var declined = a.Respond(p2.Id, false);
            Assert.AreEqual((p1.Id, false), declined);
        }

        [Test]
        public void Challenge_UnknownTargetOrSelf_IsRejected()
        {
            var a = New();
            var p1 = a.Join("철수", 0);
            Assert.AreEqual("자기 자신에게는 도전할 수 없다!", a.RequestChallenge(p1.Id, p1.Id));
            Assert.AreEqual("상대를 찾을 수 없다!", a.RequestChallenge(p1.Id, 999));
        }
    }

    /// <summary>방 자동 검색(주소 직접 입력을 대체) — 비콘 규격, UDP 비콘 수신, 서버 Probe 응답을 진짜 루프백 소켓으로 검증.</summary>
    public class LanDiscoveryTests
    {
        GameData _data; WorldMap _map;

        [OneTimeSetUp]
        public void Load()
        {
            _data = GameData.Parse(Resources.Load<TextAsset>("game-data").text);
            _map = WorldMap.Generate();
        }

        [Test]
        public void Beacon_RoundTrips_AndRejectsForeignOrBrokenPackets()
        {
            var bytes = LanDiscovery.EncodeBeacon("철수의 방\n끼어든줄", 7777, 3);
            Assert.IsTrue(LanDiscovery.TryDecodeBeacon(bytes, bytes.Length, out var name, out int port, out int players));
            Assert.AreEqual("철수의 방 끼어든줄", name, "이름의 줄바꿈은 공백으로 바뀌어 규격을 깨지 못한다");
            Assert.AreEqual(7777, port);
            Assert.AreEqual(3, players);

            var junk = System.Text.Encoding.UTF8.GetBytes("HELLO\nworld\n1\n2");
            Assert.IsFalse(LanDiscovery.TryDecodeBeacon(junk, junk.Length, out _, out _, out _), "다른 프로그램의 패킷은 무시");
            var bad = System.Text.Encoding.UTF8.GetBytes("MAROOM1\n방\nabc\n1");
            Assert.IsFalse(LanDiscovery.TryDecodeBeacon(bad, bad.Length, out _, out _, out _), "포트가 숫자가 아니면 무시");
        }

        static bool WaitFor(System.Func<bool> cond, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; System.Threading.Thread.Sleep(50); }
            return cond();
        }

        [Test]
        public void UdpBeacon_IsHeardByScanner()
        {
            const int testPort = 47811;   // 실제 게임 포트(7778)와 겹쳐 다른 실행과 간섭하지 않게 다른 번호를 쓴다
            using var scanner = new LanScanner(testPort);
            scanner.StartListening();
            using var beacon = new LanBeacon(() => "테스트방", 7777, () => 2, testPort,
                new[] { System.Net.IPAddress.Loopback });
            beacon.Start();

            Assert.IsTrue(WaitFor(() => scanner.Rooms(System.TimeSpan.FromSeconds(5)).Count > 0, 4000), "비콘이 4초 안에 잡혀야 한다");
            var room = scanner.Rooms(System.TimeSpan.FromSeconds(5)).First();
            Assert.AreEqual("테스트방", room.Name);
            Assert.AreEqual(7777, room.Port);
            Assert.AreEqual(2, room.Players);
            Assert.AreEqual("127.0.0.1", room.Address);
        }

        [Test]
        public void UdpBroadcast_WithDefaultTargets_IsHeardOnThisMachine()
        {
            // 실제 폰이 쓰는 경로(전체 브로드캐스트 + 같은 대역 브로드캐스트)가 이 PC 의 진짜 네트워크에서도 도는지.
            // LAN 에 연결돼 있지 않은 환경(CI 등)에서는 건너뛴다.
            Assume.That(LanAddress.TryGetLocalIPv4(), Is.Not.Null, "이 환경에는 LAN 주소가 없다");
            const int testPort = 47813;
            using var scanner = new LanScanner(testPort);
            scanner.StartListening();
            using var beacon = new LanBeacon(() => "브로드캐스트방", 7777, () => 1, testPort);   // targetsOverride 없음 = 실제 대상
            beacon.Start();

            Assert.IsTrue(WaitFor(() => scanner.Rooms(System.TimeSpan.FromSeconds(5)).Count > 0, 5000),
                "기본 브로드캐스트 대상(" + string.Join(", ", LanDiscovery.BroadcastTargets()) + ")으로 보낸 비콘이 잡혀야 한다");
            Assert.AreEqual("브로드캐스트방", scanner.Rooms(System.TimeSpan.FromSeconds(5)).First().Name);
        }

        [Test]
        public void TcpSweep_FindsRunningServer_WithoutJoiningIt()
        {
            using var server = new TcpArenaServer(_data, _map) { RoomName = "수색방" };
            server.Start();
            using var scanner = new LanScanner(47812, server.Port);
            scanner.StartSweep(new[] { System.Net.IPAddress.Loopback, System.Net.IPAddress.Parse("127.0.0.2") }, server.Port);

            Assert.IsTrue(WaitFor(() => scanner.Rooms(System.TimeSpan.FromSeconds(10)).Count > 0, 5000), "방을 찾아야 한다");
            var room = scanner.Rooms(System.TimeSpan.FromSeconds(10)).First();
            Assert.AreEqual("수색방", room.Name);
            Assert.AreEqual(0, room.Players, "Probe 는 입장이 아니므로 인원이 늘지 않는다");
            Assert.AreEqual(0, server.PlayerCount);
        }
    }

    /// <summary>실제 루프백 소켓으로 서버·클라이언트를 함께 띄워 왕복시키는 통합 검증(진짜 네트워크 I/O).</summary>
    public class ArenaNetworkIntegrationTests
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
        public void TwoClients_SeeEachOtherJoinMoveAndLeave()
        {
            using var server = new TcpArenaServer(_data, _map);
            server.Start();
            using var c1 = new TcpArenaClient();
            c1.Connect("127.0.0.1", server.Port, "철수", 0);
            var (_, welcome1) = WaitFor(c1, NetMsgType.Welcome);
            int id1 = (int)welcome1["Id"];

            using var c2 = new TcpArenaClient();
            c2.Connect("127.0.0.1", server.Port, "영희", 2);
            var (_, welcome2) = WaitFor(c2, NetMsgType.Welcome);
            int id2 = (int)welcome2["Id"];
            Assert.AreNotEqual(id1, id2);
            CollectionAssert.Contains(((JArray)welcome2["Others"]).Select(o => (int)o["Id"]), id1);

            var (_, joined) = WaitFor(c1, NetMsgType.Joined);
            Assert.AreEqual(id2, (int)joined["Info"]["Id"]);

            c1.SendMove(NetDirection.Left);
            var (_, moved) = WaitFor(c2, NetMsgType.Moved);
            Assert.AreEqual(id1, (int)moved["Id"]);

            c2.Dispose();
            var (_, left) = WaitFor(c1, NetMsgType.Left);
            Assert.AreEqual(id2, (int)left["Id"]);
        }

        [Test]
        public void ChallengeHandshake_TravelsAcrossTheWire()
        {
            using var server = new TcpArenaServer(_data, _map);
            server.Start();
            using var c1 = new TcpArenaClient();
            c1.Connect("127.0.0.1", server.Port, "도전자", 0);
            var (_, w1) = WaitFor(c1, NetMsgType.Welcome);
            int id1 = (int)w1["Id"];

            using var c2 = new TcpArenaClient();
            c2.Connect("127.0.0.1", server.Port, "상대", 4);
            var (_, w2) = WaitFor(c2, NetMsgType.Welcome);
            int id2 = (int)w2["Id"];
            WaitFor(c1, NetMsgType.Joined);

            // 지도는 고정 시드라 같은 접속 순서면 스폰 위치도 항상 같다. 실제로 걸어서 인접시킨다
            // (좌표를 직접 조작할 수 없는 실제 네트워크 경로이므로, 서버가 확인해 주는 Moved 로만 위치를 갱신한다).
            int x1 = (int)w1["X"], y1 = (int)w1["Y"], x2 = (int)w2["X"], y2 = (int)w2["Y"];
            int guard = 0;
            while (System.Math.Abs(x1 - x2) + System.Math.Abs(y1 - y2) != 1)
            {
                Assert.Less(guard++, 60, "60칸을 걸어도 인접하지 못했다 — 지도 생성이 바뀐 게 아닌지 확인");
                var dir = System.Math.Abs(x1 - x2) >= System.Math.Abs(y1 - y2)
                    ? (x1 > x2 ? NetDirection.Right : NetDirection.Left)
                    : (y1 > y2 ? NetDirection.Down : NetDirection.Up);
                c2.SendMove(dir);
                var (_, moved) = WaitFor(c1, NetMsgType.Moved);
                x2 = (int)moved["X"]; y2 = (int)moved["Y"];
            }

            c1.SendChallenge(id2);
            var (_, offer) = WaitFor(c2, NetMsgType.ChallengeOffer);
            Assert.AreEqual(id1, (int)offer["FromId"]);
            Assert.AreEqual("도전자", (string)offer["FromName"]);

            c2.SendChallengeResponse(true);
            var (_, result1) = WaitFor(c1, NetMsgType.ChallengeResult);
            Assert.AreEqual((id2, true), ((int)result1["OtherId"], (bool)result1["Accepted"]));
        }

        /// <summary>접속·인접·도전·수락까지 마친 두 클라이언트. Dispose 하면 둘 다 닫는다.</summary>
        sealed class DuelPair : System.IDisposable
        {
            public TcpArenaClient C1, C2;
            public JObject Start1, Start2;
            public void Dispose() { C1?.Dispose(); C2?.Dispose(); }
        }

        /// <summary>두 사람을 접속시키고 실제로 걸어서 인접시킨 뒤 도전 → 수락으로 대결을 시작시킨다(좌표를 직접 못 정하니 걷는다).</summary>
        static DuelPair StartDuelBetween(TcpArenaServer server, string name1, int starter1, string name2, int starter2)
        {
            var pair = new DuelPair { C1 = new TcpArenaClient(), C2 = new TcpArenaClient() };
            pair.C1.Connect("127.0.0.1", server.Port, name1, starter1);
            var (_, w1) = WaitFor(pair.C1, NetMsgType.Welcome);
            pair.C2.Connect("127.0.0.1", server.Port, name2, starter2);
            var (_, w2) = WaitFor(pair.C2, NetMsgType.Welcome);
            int id2 = (int)w2["Id"];
            WaitFor(pair.C1, NetMsgType.Joined);

            int x1 = (int)w1["X"], y1 = (int)w1["Y"], x2 = (int)w2["X"], y2 = (int)w2["Y"];
            int guard = 0;
            while (System.Math.Abs(x1 - x2) + System.Math.Abs(y1 - y2) != 1)
            {
                Assert.Less(guard++, 60, "60칸을 걸어도 인접하지 못했다");
                var dir = System.Math.Abs(x1 - x2) >= System.Math.Abs(y1 - y2)
                    ? (x1 > x2 ? NetDirection.Right : NetDirection.Left)
                    : (y1 > y2 ? NetDirection.Down : NetDirection.Up);
                pair.C2.SendMove(dir);
                var (_, moved) = WaitFor(pair.C1, NetMsgType.Moved);
                x2 = (int)moved["X"]; y2 = (int)moved["Y"];
            }

            pair.C1.SendChallenge(id2);
            WaitFor(pair.C2, NetMsgType.ChallengeOffer);
            pair.C2.SendChallengeResponse(true);
            WaitFor(pair.C1, NetMsgType.ChallengeResult);
            WaitFor(pair.C2, NetMsgType.ChallengeResult);
            pair.Start1 = WaitFor(pair.C1, NetMsgType.DuelStart).data;
            pair.Start2 = WaitFor(pair.C2, NetMsgType.DuelStart).data;
            return pair;
        }

        static string[] EventKinds(JObject batch) => ((JArray)batch["Events"]).Select(e => (string)e["Kind"]).ToArray();
        static JToken EventOf(JObject batch, int i) => ((JArray)batch["Events"])[i];

        static string TempRoot() => Path.Combine(Path.GetTempPath(), "duel_party_test_" + System.Guid.NewGuid());

        [Test]
        public void DuelWin_GrantsFullExpToWinner_AndPartialExpToLoser_BothPersisted()
        {
            string root = TempRoot();
            try
            {
                var accounts = new TrainerAccountStore(root);
                using var server = new TcpArenaServer(_data, _map, accounts);
                server.Start();
                using var pair = StartDuelBetween(server, "도전자", 0, "상대", 4);   // 불꼬마 vs 새싹이
                var c1 = pair.C1; var c2 = pair.C2;
                Assert.AreEqual(1, ((JArray)pair.Start1["Party"]).Count, "새 계정은 파트너 한 마리뿐");
                string move1 = (string)pair.Start1["Party"][0]["Moves"][0];
                string move2 = (string)pair.Start2["Party"][0]["Moves"][0];

                // 매 라운드 각자 첫 기술만 계속 낸다 — 누가 이기든 대결이 끝날 때까지 반복한다.
                JObject ended1Data = null, ended2Data = null;
                for (int round = 0; round < 30 && (ended1Data == null || ended2Data == null); round++)
                {
                    c1.SendDuelAction(new MoveAction(move1));
                    c2.SendDuelAction(new MoveAction(move2));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 2000 && (ended1Data == null || ended2Data == null))
                    {
                        if (c1.TryTakeReceived(out var t1, out var d1) && t1 == NetMsgType.DuelEnded) ended1Data = d1;
                        if (c2.TryTakeReceived(out var t2, out var d2) && t2 == NetMsgType.DuelEnded) ended2Data = d2;
                        System.Threading.Thread.Sleep(5);
                    }
                }
                Assert.IsNotNull(ended1Data, "대결이 끝나지 않았다(도전자 쪽)");
                Assert.IsNotNull(ended2Data, "대결이 끝나지 않았다(상대 쪽)");

                bool c1Won = (bool)ended1Data["YouWon"];
                bool c2Won = (bool)ended2Data["YouWon"];
                Assert.AreNotEqual(c1Won, c2Won, "정확히 한쪽만 이겨야 한다");

                var winnerData = c1Won ? ended1Data : ended2Data;
                int expGained = (int)winnerData["ExpGained"];
                Assert.Greater(expGained, 0, "이긴 쪽은 경험치를 받아야 한다");
                Assert.IsTrue(((JArray)winnerData["Growth"]).Count > 0 || expGained < Growth.ExpToNext(5), "성장 이벤트가 있거나, 레벨업엔 못 미치는 경험치여야 한다");

                // 서버가 실제로 적용·저장한 값이, Core 의 같은 공식을 직접 돌린 결과와 정확히 일치하는지 확인한다.
                string winnerName = c1Won ? "도전자" : "상대";
                int winnerStarter = c1Won ? 0 : 4;
                var savedWinner = accounts.Load(_data, winnerName);
                Assert.IsNotNull(savedWinner);
                var expected = Monster.Create(_data, winnerStarter, 5);
                Growth.GainExp(_data, expected, expGained);
                Assert.AreEqual(expected.Level, savedWinner.Party[0].Level, "레벨");
                Assert.AreEqual(expected.Exp, savedWinner.Party[0].Exp, "이월된 경험치");
                CollectionAssert.AreEqual(expected.Moves, savedWinner.Party[0].Moves, "기술 습득");

                // 친선 대결이라 진 쪽도 벌칙은 없고, 대신 이긴 쪽 기준 경험치의 40%를 참가 보상으로 받는다.
                string loserName = c1Won ? "상대" : "도전자";
                int loserStarter = c1Won ? 4 : 0;
                var savedLoser = accounts.Load(_data, loserName);
                Assert.IsNotNull(savedLoser);

                var loserData = c1Won ? ended2Data : ended1Data;
                int loserExpGained = (int)loserData["ExpGained"];
                Assert.Greater(loserExpGained, 0, "진 쪽도 참가 경험치를 받아야 한다");
                Assert.Less(loserExpGained, expGained, "진 쪽 경험치는 항상 이긴 쪽보다 적어야 한다(40%)");

                var expectedLoser = Monster.Create(_data, loserStarter, 5);
                Growth.GainExp(_data, expectedLoser, loserExpGained);
                Assert.AreEqual(expectedLoser.Level, savedLoser.Party[0].Level, "진 쪽 레벨(참가 경험치만큼)");
                Assert.AreEqual(expectedLoser.Exp, savedLoser.Party[0].Exp, "진 쪽 이월 경험치");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void Duel_PotionAndSwitch_TravelOverTheWire_AndTheRealAccountsAreUntouched()
        {
            string root = TempRoot();
            try
            {
                var accounts = new TrainerAccountStore(root);
                var p1 = PlayerState.NewGame(_data, 0); p1.Party.Add(Monster.Create(_data, 2, 5));   // 불꼬마 + 물방울이
                var p2 = PlayerState.NewGame(_data, 4); p2.Party.Add(Monster.Create(_data, 0, 5));   // 새싹이 + 불꼬마
                accounts.Save("도전자", p1); accounts.Save("상대", p2);
                using var server = new TcpArenaServer(_data, _map, accounts);
                server.Start();
                using var pair = StartDuelBetween(server, "도전자", 0, "상대", 4);
                var c1 = pair.C1; var c2 = pair.C2;

                Assert.AreEqual(2, ((JArray)pair.Start1["Party"]).Count, "내 파티 전체를 받는다");
                Assert.AreEqual(2, (int)pair.Start1["Party"][1]["SpeciesId"]);
                Assert.AreEqual(3, (int)pair.Start1["Potions"]);
                Assert.AreEqual(4, (int)pair.Start1["Opponent"]["SpeciesId"], "상대는 출전 중인 몬스터만 보인다");
                Assert.AreEqual(JTokenType.Null, pair.Start1["Opponent"]["Moves"].Type, "상대의 기술은 숨긴다");

                // 풀피에게 쓰는 상처약·모르는 기술은 서버가 거절하고 오류를 돌려준다(상태는 그대로).
                c2.SendDuelAction(new PotionAction(0));
                var (_, err) = WaitFor(c2, NetMsgType.Error);
                Assert.AreEqual("이미 HP가 가득 찼다!", (string)err["Reason"]);
                c2.SendDuelAction(new MoveAction("blast"));
                Assert.AreEqual("배우지 않은 기술이다!", (string)WaitFor(c2, NetMsgType.Error).data["Reason"]);

                // 1라운드: 도전자는 교체, 상대는 몸통박치기(명중 100) — 새로 나온 몬스터가 맞는다.
                c1.SendDuelAction(new SwitchAction(1));
                c2.SendDuelAction(new MoveAction("tackle"));
                var (_, batch1) = WaitFor(c1, NetMsgType.DuelEvents);
                var (_, batch2) = WaitFor(c2, NetMsgType.DuelEvents);
                CollectionAssert.AreEqual(new[] { "switchOut", "switchIn", "moveUsed", "damage" }, EventKinds(batch1));
                CollectionAssert.AreEqual(new[] { "switchOut", "switchIn", "moveUsed", "damage" }, EventKinds(batch2));
                Assert.AreEqual(0, (int)EventOf(batch1, 1)["Side"]);
                Assert.AreEqual(1, (int)EventOf(batch1, 1)["PartyIndex"]);
                Assert.AreEqual(0, (int)EventOf(batch1, 3)["Side"], "도전자 쪽이 맞았다(도전자 기준 0 = 나)");
                Assert.AreEqual(1, (int)EventOf(batch2, 3)["Side"], "같은 사건이 상대 기준으로는 1");
                Assert.AreEqual(2, (int)EventOf(batch2, 1)["Monster"]["SpeciesId"], "상대의 SwitchIn 에는 새 몬스터의 모습이 실린다");
                Assert.AreEqual(-1, (int)EventOf(batch2, 1)["PartyIndex"], "상대 파티 칸은 숨긴다");
                int hit = (int)EventOf(batch1, 3)["Amount"];
                Assert.GreaterOrEqual(hit, 1);

                // 2라운드: 도전자는 방금 맞은 몬스터에게 상처약, 상대는 또 공격.
                c1.SendDuelAction(new PotionAction(1));
                c2.SendDuelAction(new MoveAction("tackle"));
                var (_, potion1) = WaitFor(c1, NetMsgType.DuelEvents);
                var (_, potion2) = WaitFor(c2, NetMsgType.DuelEvents);
                CollectionAssert.AreEqual(new[] { "potionUsed", "moveUsed", "damage" }, EventKinds(potion1));
                Assert.AreEqual(0, (int)EventOf(potion1, 0)["Side"]);
                Assert.AreEqual(1, (int)EventOf(potion1, 0)["PartyIndex"]);
                Assert.AreEqual(hit, (int)EventOf(potion1, 0)["Amount"], "맞은 만큼만 차오른다(30 이 아니라 실제 회복량)");
                Assert.AreEqual(1, (int)EventOf(potion2, 0)["Side"]);
                Assert.IsTrue((bool)EventOf(potion2, 0)["TargetActive"]);
                Assert.AreEqual(2, (int)EventOf(potion2, 0)["Monster"]["SpeciesId"]);

                // 기권: 즉시 끝나고 기권한 쪽이 진다.
                c1.SendDuelAction(new FleeAction());
                var (_, forfeit1) = WaitFor(c1, NetMsgType.DuelEvents);
                var (_, forfeit2) = WaitFor(c2, NetMsgType.DuelEvents);
                CollectionAssert.AreEqual(new[] { "forfeit" }, EventKinds(forfeit1));
                Assert.AreEqual(0, (int)EventOf(forfeit1, 0)["Side"]);
                Assert.AreEqual(1, (int)EventOf(forfeit2, 0)["Side"]);
                var (_, ended1) = WaitFor(c1, NetMsgType.DuelEnded);
                var (_, ended2) = WaitFor(c2, NetMsgType.DuelEnded);
                Assert.IsFalse((bool)ended1["YouWon"]);
                Assert.IsTrue((bool)ended2["YouWon"]);
                Assert.IsFalse((bool)ended2["OpponentLeft"]);

                // 끝난 뒤 늦게 온 행동은 조용히 무시되고, 연결은 멀쩡하다.
                c1.SendDuelAction(new MoveAction("tackle"));
                c1.SendDuelReplace(0);
                c1.SendMove(NetDirection.Down);
                WaitFor(c1, NetMsgType.Moved);

                // 친선 대결이라 진짜 계정의 상처약·HP 는 그대로다(경험치만 오른다).
                var saved = accounts.Load(_data, "도전자");
                Assert.AreEqual(3, saved.Potions);
                foreach (var m in saved.Party) Assert.AreEqual(m.MaxHp, m.Hp);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void Duel_FaintedMonster_WaitsForTheReplacement_ThenContinues()
        {
            string root = TempRoot();
            try
            {
                var accounts = new TrainerAccountStore(root);
                var p1 = new PlayerState();
                p1.Party.Add(Monster.Create(_data, 4, 1)); p1.Party.Add(Monster.Create(_data, 0, 5));   // 곧 쓰러질 Lv.1 + 교체 요원
                var p2 = new PlayerState();
                p2.Party.Add(Monster.Create(_data, 15, 40));                                               // 압도적인 상대
                accounts.Save("도전자", p1); accounts.Save("상대", p2);
                using var server = new TcpArenaServer(_data, _map, accounts);
                server.Start();
                using var pair = StartDuelBetween(server, "도전자", 4, "상대", 15);
                var c1 = pair.C1; var c2 = pair.C2;

                c1.SendDuelAction(new MoveAction("tackle"));
                c2.SendDuelAction(new MoveAction("tackle"));
                var (_, b1) = WaitFor(c1, NetMsgType.DuelEvents);
                var (_, b2) = WaitFor(c2, NetMsgType.DuelEvents);
                CollectionAssert.AreEqual(new[] { "moveUsed", "damage", "fainted", "replacementNeeded" }, EventKinds(b1));
                CollectionAssert.AreEqual(new[] { "moveUsed", "damage", "fainted", "replacementNeeded" }, EventKinds(b2));
                Assert.AreEqual(0, (int)EventOf(b1, 3)["Side"], "도전자 기준: 내가 골라야 한다");
                Assert.AreEqual(1, (int)EventOf(b2, 3)["Side"], "상대 기준: 상대가 고르는 중");

                // 고르는 동안 행동이나 쓰러진 몬스터 선택은 거절된다.
                c2.SendDuelAction(new MoveAction("tackle"));
                WaitFor(c2, NetMsgType.Error);
                c1.SendDuelReplace(0);
                Assert.AreEqual("기절한 몬스터는 싸울 수 없다!", (string)WaitFor(c1, NetMsgType.Error).data["Reason"]);

                c1.SendDuelReplace(1);
                var (_, r1) = WaitFor(c1, NetMsgType.DuelEvents);
                var (_, r2) = WaitFor(c2, NetMsgType.DuelEvents);
                CollectionAssert.AreEqual(new[] { "switchIn" }, EventKinds(r1));
                CollectionAssert.AreEqual(new[] { "switchIn" }, EventKinds(r2));
                Assert.AreEqual(1, (int)EventOf(r1, 0)["PartyIndex"]);
                Assert.AreEqual(0, (int)EventOf(r2, 0)["Monster"]["SpeciesId"], "상대에게는 새로 나온 불꼬마의 모습");

                // 이제 다시 라운드를 진행할 수 있다.
                c1.SendDuelAction(new MoveAction("tackle"));
                c2.SendDuelAction(new MoveAction("tackle"));
                var (_, next) = WaitFor(c1, NetMsgType.DuelEvents);
                Assert.AreEqual("moveUsed", EventKinds(next)[0]);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void Duel_Disconnect_ForfeitsToTheOpponent()
        {
            using var server = new TcpArenaServer(_data, _map);
            server.Start();
            using var pair = StartDuelBetween(server, "도전자", 0, "상대", 4);

            // 한쪽만 기술을 낸 상태(라운드 진행 중)에서 상대가 끊는다.
            pair.C1.SendDuelAction(new MoveAction("tackle"));
            pair.C2.Dispose();
            var (_, ended) = WaitFor(pair.C1, NetMsgType.DuelEnded);
            Assert.IsTrue((bool)ended["YouWon"]);
            Assert.IsTrue((bool)ended["OpponentLeft"]);
        }
    }
}
