using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MonsterAdventure.Net
{
    /// <summary>호스트가 "다른 사람은 이 주소로 접속하세요"를 보여줄 때 쓰는, 이 기기의 LAN용 IPv4 주소.
    /// Dns.GetHostEntry(호스트명)은 안드로이드에서 비거나 엉뚱한 주소를 주는 일이 많아서, 실제 네트워크 인터페이스를
    /// 훑어 사설 대역(192.168.x.x / 10.x.x.x / 172.16~31.x.x) 주소를 찾는다. Wi-Fi/이더넷 인터페이스를 우선하고
    /// 모바일 데이터(rmnet 등)는 뒤로 미룬다.</summary>
    public static class LanAddress
    {
        public static string TryGetLocalIPv4()
        {
            // 1) 네트워크 인터페이스 목록. 새 안드로이드(11+)에서는 이 목록을 못 읽고 예외를 던지는 기기가 있어서,
            //    어떤 예외든 삼키고 다음 방법으로 넘어간다.
            try
            {
                var candidates = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses
                        .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                        .Select(u => (Iface: n, Addr: u.Address)))
                    .Where(x => IsPrivate(x.Addr))
                    .OrderBy(x => IsMobileData(x.Iface) ? 1 : 0)
                    .ToList();
                if (candidates.Count > 0) return candidates[0].Addr.ToString();
            }
            catch (Exception) { /* 아래 방법으로 */ }

            // 2) "이 기기에서 밖으로 나갈 때 쓰는 주소"를 OS 에게 묻는 방법. UDP 소켓을 연결(Connect)만 하면 실제 패킷은
            //    나가지 않고, OS 가 경로를 골라 LocalEndPoint 에 자기 주소를 채워 준다. 인터페이스 목록이 막혀도 동작한다.
            foreach (var probe in new[] { "192.168.0.1", "192.168.1.1", "10.0.0.1", "172.20.10.1", "8.8.8.8" })
            {
                try
                {
                    using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        s.Connect(probe, 65530);
                        var ip = ((IPEndPoint)s.LocalEndPoint).Address;
                        if (!IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && IsPrivate(ip)) return ip.ToString();
                    }
                }
                catch (Exception) { /* 다음 후보 */ }
            }

            // 3) 마지막 수단: 호스트 이름 조회(안드로이드에선 비는 경우가 많다).
            try
            {
                return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    ?.ToString();
            }
            catch (Exception) { return null; }
        }

        static bool IsPrivate(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168);
        }

        static bool IsMobileData(NetworkInterface n)
        {
            string name = n.Name.ToLowerInvariant();
            return name.StartsWith("rmnet") || name.StartsWith("ccmni") || name.StartsWith("pdp") || name.StartsWith("wwan");
        }
    }
}
