using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DipWd;

static class LanIp
{
    public static IPAddress GetPreferred()
    {
        IPAddress? best = null;
        IPAddress? fallback = null;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var name = ni.Name + " " + ni.Description;
                var isVpn = name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("WARP", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase);

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    // Prefer real LAN over CGNAT/VPN (100.64/10)
                    var cgnat = b[0] == 100 && b[1] >= 64 && b[1] <= 127;
                    if (isVpn || cgnat)
                    {
                        fallback ??= ua.Address;
                        continue;
                    }
                    if (b[0] == 192 && b[1] == 168) return ua.Address;
                    if (b[0] == 10) { best ??= ua.Address; continue; }
                    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) { best ??= ua.Address; continue; }
                    best ??= ua.Address;
                }
            }
        }
        catch { }

        if (best is not null) return best;

        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 53);
            if (s.LocalEndPoint is IPEndPoint ep && !IsCgnat(ep.Address))
                return ep.Address;
        }
        catch { }

        return fallback ?? IPAddress.Loopback;
    }

    static bool IsCgnat(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
}
