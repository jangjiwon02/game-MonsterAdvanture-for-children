using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace MonsterAdventure.Net
{
    /// <summary>호스트가 "다른 사람은 이 주소로 접속하세요"를 보여줄 때 쓰는, 이 기기의 LAN용 IPv4 주소.</summary>
    public static class LanAddress
    {
        public static string TryGetLocalIPv4()
        {
            try
            {
                return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    ?.ToString();
            }
            catch (SocketException) { return null; }
        }
    }
}
