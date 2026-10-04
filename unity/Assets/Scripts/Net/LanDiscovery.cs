using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MonsterAdventure.Net
{
    /// <summary>검색에 잡힌 방 하나.</summary>
    public sealed class DiscoveredRoom
    {
        public string Name;
        public string Address;
        public int Port;
        public int Players;
        public DateTime LastSeenUtc;
        public string Key => $"{Address}:{Port}";
    }

    /// <summary>
    /// 같은 Wi-Fi 안에서 방을 자동으로 찾기 위한 규칙(주소를 손으로 입력하는 방식을 대체한다).
    /// 두 갈래로 찾는다 — ① 호스트가 1초마다 UDP 로 "여기 방 있어요" 를 뿌리고(LanBeacon), 찾는 쪽이 그걸 듣는다.
    /// ② 일부 공유기·기기는 UDP 브로드캐스트를 막으므로, 찾는 쪽이 같은 대역(x.x.x.1~254)의 게임 포트를 직접 두드려
    /// 서버에게 방 이름·인원을 물어본다(Probe). 둘 다 같은 목록(DiscoveredRoom)에 합쳐진다.
    /// </summary>
    public static class LanDiscovery
    {
        public const int DefaultPort = 7778;
        const string Magic = "MAROOM1";

        public static byte[] EncodeBeacon(string name, int tcpPort, int players)
        {
            string clean = (name ?? "").Replace("\r", " ").Replace("\n", " ");
            return Encoding.UTF8.GetBytes($"{Magic}\n{clean}\n{tcpPort}\n{players}");
        }

        /// <summary>우리 게임의 비콘이 아니거나 깨졌으면 false.</summary>
        public static bool TryDecodeBeacon(byte[] data, int length, out string name, out int tcpPort, out int players)
        {
            name = null; tcpPort = 0; players = 0;
            try
            {
                var parts = Encoding.UTF8.GetString(data, 0, length).Split('\n');
                if (parts.Length != 4 || parts[0] != Magic) return false;
                name = parts[1];
                return int.TryParse(parts[2], out tcpPort) && tcpPort > 0 && tcpPort < 65536 && int.TryParse(parts[3], out players);
            }
            catch (ArgumentException) { return false; }
        }

        /// <summary>비콘을 보낼 곳: 전체 브로드캐스트 + 이 기기가 속한 대역의 "방향성" 브로드캐스트(x.x.x.255).
        /// 255.255.255.255 만으로는 못 나가는 기기가 있어서 둘 다 쓴다.</summary>
        public static List<IPAddress> BroadcastTargets()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            string own = LanAddress.TryGetLocalIPv4();
            if (own != null && IPAddress.TryParse(own, out var ip))
            {
                var b = ip.GetAddressBytes();
                list.Add(new IPAddress(new byte[] { b[0], b[1], b[2], 255 }));   // 가정집·휴대폰 핫스팟은 거의 /24
            }
            return list.Distinct().ToList();
        }

        /// <summary>이 기기와 같은 /24 대역의 다른 주소들(자기 자신 제외).</summary>
        public static List<IPAddress> SubnetHosts()
        {
            var result = new List<IPAddress>();
            string own = LanAddress.TryGetLocalIPv4();
            if (own == null || !IPAddress.TryParse(own, out var ip)) return result;
            var b = ip.GetAddressBytes();
            for (int i = 1; i <= 254; i++)
                if (i != b[3]) result.Add(new IPAddress(new byte[] { b[0], b[1], b[2], (byte)i }));
            return result;
        }
    }

    /// <summary>호스트 쪽: 방이 열려 있는 동안 1초마다 UDP 로 방 정보를 뿌린다.</summary>
    public sealed class LanBeacon : IDisposable
    {
        readonly Func<string> _name;
        readonly int _tcpPort;
        readonly Func<int> _players;
        readonly int _discoveryPort;
        readonly IList<IPAddress> _targetsOverride;
        UdpClient _udp;
        Thread _thread;
        volatile bool _running;

        public LanBeacon(Func<string> name, int tcpPort, Func<int> players,
            int discoveryPort = LanDiscovery.DefaultPort, IList<IPAddress> targetsOverride = null)
        {
            _name = name; _tcpPort = tcpPort; _players = players;
            _discoveryPort = discoveryPort; _targetsOverride = targetsOverride;
        }

        public void Start()
        {
            _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "LanBeacon" };
            _thread.Start();
        }

        void Loop()
        {
            while (_running)
            {
                try
                {
                    var packet = LanDiscovery.EncodeBeacon(_name(), _tcpPort, _players());
                    // 네트워크가 바뀔 수 있어서(와이파이 재연결 등) 매번 대상을 다시 구한다.
                    foreach (var target in _targetsOverride ?? LanDiscovery.BroadcastTargets())
                    {
                        try { _udp.Send(packet, packet.Length, new IPEndPoint(target, _discoveryPort)); }
                        catch (SocketException) { /* 이 대상으로만 못 나간 것 — 다른 대상은 계속 시도 */ }
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { /* 비콘은 부가 기능 — 실패해도 게임 서버엔 영향 없음 */ }
                for (int i = 0; i < 10 && _running; i++) Thread.Sleep(100);
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _udp?.Close(); } catch (Exception) { /* 무시 */ }
        }
    }

    /// <summary>찾는 쪽: 비콘을 듣고, 필요하면 같은 대역의 게임 포트를 직접 두드려(Probe) 방을 모은다.</summary>
    public sealed class LanScanner : IDisposable
    {
        readonly int _discoveryPort;
        readonly int _tcpPort;
        readonly Dictionary<string, DiscoveredRoom> _rooms = new Dictionary<string, DiscoveredRoom>();
        readonly object _lock = new object();
        UdpClient _udp;
        Thread _listenThread;
        volatile bool _running;
        volatile int _sweepsRunning;

        public bool SweepRunning => _sweepsRunning > 0;

        public LanScanner(int discoveryPort = LanDiscovery.DefaultPort, int tcpPort = 7777)
        {
            _discoveryPort = discoveryPort; _tcpPort = tcpPort;
        }

        public void StartListening()
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            // 같은 기기에서 호스트 비콘과 검색이 한 포트를 같이 쓸 수 있게(테스트·한 기기 두 앱).
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
            _running = true;
            _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "LanScanner.Listen" };
            _listenThread.Start();
        }

        void ListenLoop()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    var data = _udp.Receive(ref from);
                    if (LanDiscovery.TryDecodeBeacon(data, data.Length, out var name, out int port, out int players))
                        Add(from.Address.ToString(), port, name, players);
                }
                catch (SocketException) { if (!_running) return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        void Add(string address, int port, string name, int players)
        {
            var room = new DiscoveredRoom { Address = address, Port = port, Name = name, Players = players, LastSeenUtc = DateTime.UtcNow };
            lock (_lock) _rooms[room.Key] = room;
        }

        /// <summary>같은 대역(또는 targets)의 게임 포트를 두드려 방을 찾는다. 백그라운드로 돌고 SweepRunning 으로 끝났는지 본다.</summary>
        public void StartSweep(IEnumerable<IPAddress> targets = null, int? tcpPort = null)
        {
            var list = (targets ?? LanDiscovery.SubnetHosts()).ToList();
            int port = tcpPort ?? _tcpPort;
            Interlocked.Increment(ref _sweepsRunning);
            Task.Run(async () =>
            {
                try
                {
                    using (var gate = new SemaphoreSlim(48))
                    {
                        await Task.WhenAll(list.Select(async ip =>
                        {
                            await gate.WaitAsync();
                            try { await Task.Run(() => Probe(ip, port)); }
                            finally { gate.Release(); }
                        }));
                    }
                }
                catch (Exception) { /* 검색은 부가 기능 */ }
                finally { Interlocked.Decrement(ref _sweepsRunning); }
            });
        }

        void Probe(IPAddress ip, int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.BeginConnect(ip, port, null, null);
                    if (!connect.AsyncWaitHandle.WaitOne(700)) return;
                    client.EndConnect(connect);
                    client.ReceiveTimeout = 900;
                    var stream = client.GetStream();
                    var probe = Frame.Encode(NetCodec.Encode(NetMsgType.Probe, new object()));
                    stream.Write(probe, 0, probe.Length);

                    var reader = new Frame.Reader();
                    var buf = new byte[1024];
                    for (int guard = 0; guard < 8; guard++)
                    {
                        if (reader.TryTake(out var json))
                        {
                            if (NetCodec.TryDecode(json, out var type, out var d) && type == NetMsgType.RoomInfo)
                            {
                                var info = d.ToObject<RoomInfoMessage>();
                                Add(ip.ToString(), port, info.Name, info.Players);
                            }
                            return;
                        }
                        int n = stream.Read(buf, 0, buf.Length);
                        if (n <= 0) return;
                        reader.Feed(buf, n);
                    }
                }
            }
            catch (Exception) { /* 그 주소엔 우리 방이 없다 */ }
        }

        /// <summary>최근 maxAge 안에 확인된 방들(이름순). 호스트가 꺼지면 목록에서 사라지게 한다.</summary>
        public List<DiscoveredRoom> Rooms(TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            lock (_lock) return _rooms.Values.Where(r => r.LastSeenUtc >= cutoff).OrderBy(r => r.Name).ToList();
        }

        public void Dispose()
        {
            _running = false;
            try { _udp?.Close(); } catch (Exception) { /* 무시 */ }
        }
    }
}
