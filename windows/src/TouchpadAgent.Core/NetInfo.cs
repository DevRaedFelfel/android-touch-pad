// Which remote addresses may talk to the agent, and which LAN IP to show (agent/netinfo.py).
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TouchpadAgent.Core;

public static class NetInfo
{
    // Only local-network peers; loopback is allowed so the agent can be tested from the same machine.
    private static readonly (IPAddress net, int bits)[] Allowed =
    {
        (IPAddress.Parse("10.0.0.0"), 8), (IPAddress.Parse("172.16.0.0"), 12), (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("169.254.0.0"), 16), (IPAddress.Parse("127.0.0.0"), 8), (IPAddress.Parse("::1"), 128),
        (IPAddress.Parse("fe80::"), 10), (IPAddress.Parse("fc00::"), 7),
    };

    public static bool IsAllowedIp(IPAddress? ip)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        foreach (var (net, bits) in Allowed)
        {
            if (net.AddressFamily != ip.AddressFamily) continue;
            if (PrefixMatches(bytes, net.GetAddressBytes(), bits)) return true;
        }
        return false;
    }

    public static bool IsAllowedIp(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int zone = text.IndexOf('%');
        if (zone >= 0) text = text[..zone]; // drop IPv6 zone id
        return IPAddress.TryParse(text, out var ip) && IsAllowedIp(ip);
    }

    private static bool PrefixMatches(byte[] a, byte[] b, int bits)
    {
        for (int i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            int mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }

    private static bool IsLan(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetwork && IsAllowedIp(ip) && !IPAddress.IsLoopback(ip);

    /// <summary>Private IPv4 addresses of this machine (best effort).</summary>
    public static List<string> PrivateIps()
    {
        var found = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (IsLan(ua.Address) && !found.Contains(ua.Address.ToString())) found.Add(ua.Address.ToString());
            }
        }
        catch (NetworkInformationException) { }
        return found;
    }

    /// <summary>The IP the OS would use to reach the local network, or null.</summary>
    public static string? LanIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(IPAddress.Parse("8.8.8.8"), 80); // UDP connect selects a route and sends nothing
            if (s.LocalEndPoint is IPEndPoint ep && IsLan(ep.Address)) return ep.Address.ToString();
        }
        catch (SocketException) { }
        return PrivateIps().FirstOrDefault();
    }
}
