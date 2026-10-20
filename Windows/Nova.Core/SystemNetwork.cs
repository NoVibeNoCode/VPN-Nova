using System.Net.NetworkInformation;

namespace Nova.Core;

/// Other VPNs on the PC. A connected VPN adapter (another proxy client's TUN, WireGuard, OpenVPN,
/// a built-in Windows VPN connection) competes with Nova's routes, so the app warns about it.
public static class SystemNetwork
{
    public sealed record Adapter(string Name, string Description, NetworkInterfaceType Type, bool Up);

    /// Names of VPN adapters that are up right now, e.g. `["WireGuard Tunnel"]`.
    public static List<string> ConnectedVpns()
    {
        try
        {
            return ParseConnected(NetworkInterface.GetAllNetworkInterfaces()
                .Select(n => new Adapter(n.Name, n.Description, n.NetworkInterfaceType, n.OperationalStatus == OperationalStatus.Up)));
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    static readonly string[] VpnMarkers =
        ["wintun", "wireguard", "tap-windows", "openvpn", "vpn", "tunnel", "zerotier", "tailscale", "fortinet", "anyconnect", "mihomo", "sing-box"];

    /// Up adapters that look like a VPN, except Nova's own (`ConfigBuilder.TunInterfaceName`) and
    /// Windows' built-in pseudo-tunnels (Teredo, 6to4, IP-HTTPS).
    public static List<string> ParseConnected(IEnumerable<Adapter> adapters)
    {
        var names = new List<string>();
        foreach (var a in adapters)
        {
            if (!a.Up || a.Type == NetworkInterfaceType.Loopback) continue;
            if (string.Equals(a.Name, ConfigBuilder.TunInterfaceName, StringComparison.OrdinalIgnoreCase)) continue;
            var d = a.Description.ToLowerInvariant();
            if (d.Contains("teredo") || d.Contains("6to4") || d.Contains("ip-https") || d.Contains("isatap")) continue;
            var vpn = a.Type == NetworkInterfaceType.Ppp || VpnMarkers.Any(m => d.Contains(m) || a.Name.ToLowerInvariant().Contains(m));
            if (vpn && !names.Contains(a.Name)) names.Add(a.Name);
        }
        return names;
    }
}
