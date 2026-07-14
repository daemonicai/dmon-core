using System.Net;
using System.Net.Sockets;

namespace Dmon.Tools.Builtin;

/// <summary>
/// Classifies IP addresses against the loopback, link-local, private (RFC1918), and
/// unique-local ranges that the <c>fetch</c> tool refuses to reach, mirroring the SSRF
/// posture without depending on <c>Dmon.Core</c> (BCL-only).
/// </summary>
/// <remarks>
/// The refused ranges are IPv4 "this host" <c>0.0.0.0/8</c>, IPv4 loopback
/// <c>127.0.0.0/8</c>, IPv4 link-local <c>169.254.0.0/16</c> (including the cloud-metadata
/// address <c>169.254.169.254</c>), the RFC1918 private ranges <c>10.0.0.0/8</c>,
/// <c>172.16.0.0/12</c>, and <c>192.168.0.0/16</c>, IPv6 unspecified <c>::</c>, IPv6
/// loopback <c>::1</c>, IPv6 link-local <c>fe80::/10</c>, and IPv6 unique-local
/// <c>fc00::/7</c>. IPv4-mapped IPv6 addresses are un-mapped to their embedded IPv4 form
/// before classification.
/// </remarks>
internal static class SsrfGuard
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="address"/> falls in a refused
    /// (loopback / link-local / private / unique-local) range.
    /// </summary>
    internal static bool IsRefused(IPAddress address)
    {
        IPAddress candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (candidate.AddressFamily == AddressFamily.InterNetwork)
            return IsRefusedIPv4(candidate);

        if (candidate.AddressFamily == AddressFamily.InterNetworkV6)
            return IsRefusedIPv6(candidate);

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <b>any</b> address in <paramref name="addresses"/>
    /// is refused, implementing the multi-address "refuse if any is refused" rule.
    /// </summary>
    internal static bool AnyRefused(IEnumerable<IPAddress> addresses)
    {
        foreach (IPAddress address in addresses)
        {
            if (IsRefused(address))
                return true;
        }
        return false;
    }

    private static bool IsRefusedIPv4(IPAddress address)
    {
        byte[] octets = address.GetAddressBytes();

        // 0.0.0.0/8 "this host" — 0.0.0.0 routes to loopback on Linux
        if (octets[0] == 0)
            return true;

        // 127.0.0.0/8 loopback
        if (octets[0] == 127)
            return true;

        // 169.254.0.0/16 link-local (incl. 169.254.169.254 cloud metadata)
        if (octets[0] == 169 && octets[1] == 254)
            return true;

        // 10.0.0.0/8
        if (octets[0] == 10)
            return true;

        // 172.16.0.0/12 (172.16.x.x – 172.31.x.x)
        if (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
            return true;

        // 192.168.0.0/16
        if (octets[0] == 192 && octets[1] == 168)
            return true;

        return false;
    }

    private static bool IsRefusedIPv6(IPAddress address)
    {
        // :: unspecified/all-zeros — routes to loopback like 0.0.0.0
        if (address.Equals(IPAddress.IPv6Any))
            return true;

        // ::1 loopback
        if (IPAddress.IsLoopback(address))
            return true;

        byte[] bytes = address.GetAddressBytes();

        // fe80::/10 link-local: first 10 bits are 1111 1110 10.
        if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            return true;

        // fc00::/7 unique-local: first 7 bits are 1111 110.
        if ((bytes[0] & 0xfe) == 0xfc)
            return true;

        return false;
    }
}
