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

                return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    ?.ToString();
            }
            catch (Exception e) when (e is SocketException || e is NetworkInformationException) { return null; }
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
