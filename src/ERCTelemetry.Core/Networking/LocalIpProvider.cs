using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ERCTelemetry.Core.Networking;

/// <summary>One candidate IPv4 address of this machine on the local network.</summary>
/// <param name="Address">Dotted-quad IPv4 string.</param>
/// <param name="HasGateway">True when the owning interface has a default gateway —
/// the typical wired/Wi-Fi LAN adapter a console shares the network with.</param>
public sealed record LanIpCandidate(string Address, bool HasGateway);

/// <summary>Finds the LAN IPv4 address(es) a console (or second PC) should send UDP
/// telemetry to. Selection logic is pure and testable; NIC enumeration is a thin wrapper.</summary>
public static class LocalIpProvider
{
    /// <summary>Returns this machine's routable IPv4 addresses, best candidate first —
    /// the address to enter in the game's UDP telemetry settings on a console.
    /// Loopback and APIPA (169.254.x.x, "no DHCP") addresses can never reach a console
    /// and are excluded. Empty when the machine has no usable network.</summary>
    public static IReadOnlyList<string> GetLanIPv4Addresses()
    {
        var candidates = new List<LanIpCandidate>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Count > 0;
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        candidates.Add(new LanIpCandidate(unicast.Address.ToString(), hasGateway));
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // NIC enumeration failed (rare) — return whatever we gathered so far
            // instead of crashing the UI refresh.
        }

        return SelectOrdered(candidates);
    }

    /// <summary>The single best address to enter in the game, or null when none exists.</summary>
    public static string? GetPreferredLanIPv4() => GetLanIPv4Addresses().FirstOrDefault();

    /// <summary>Orders candidates for console use: gateway-connected interfaces first
    /// (that is the adapter the console talks to), then alphabetically for stable output;
    /// non-routable addresses (loopback, APIPA, unspecified) are dropped.</summary>
    public static IReadOnlyList<string> SelectOrdered(IEnumerable<LanIpCandidate> candidates) =>
        candidates
            .Where(c => IsRoutable(c.Address))
            .OrderByDescending(c => c.HasGateway)
            .ThenBy(c => c.Address, StringComparer.Ordinal)
            .Select(c => c.Address)
            .ToArray();

    /// <summary>True for a normal unicast IPv4 — not loopback (127.x), not APIPA
    /// (169.254.x.x), not the unspecified address.</summary>
    internal static bool IsRoutable(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();
        return bytes[0] is not (127 or 0) && !(bytes[0] == 169 && bytes[1] == 254);
    }
}