using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MonsterAdventure.Net
{
    /// <summary>호스트가 "다른 사람은 이 주소로 접속하세요"를 보여줄 때 쓰는, 이 기기의 LAN용 IPv4 주소.</summary>
    public static class LanAddress
    {
        public static string TryGetLocalIPv4()
        {
            // 안드로이드에서는 Dns.GetHostEntry(Dns.GetHostName())가 "localhost"만 돌려주고 실제
            // Wi-Fi 주소는 못 찾는 경우가 흔하다 — 활성화된 네트워크 인터페이스를 직접 뒤져서 찾는다.
            try
            {
                var fromInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                        && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                if (fromInterfaces != null) return fromInterfaces.ToString();
            }
            catch (Exception) { /* 일부 플랫폼에서 NetworkInterface API 자체가 막혀 있을 수 있다 — 아래로 폴백 */ }

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
