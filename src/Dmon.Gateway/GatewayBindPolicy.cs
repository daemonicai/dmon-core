using System.Net;

namespace Dmon.Gateway;

/// <summary>
/// Validates the gateway bind address against the loopback-by-default policy (D5 / ADR-012).
///
/// Rules:
///   - Loopback (127.x.x.x, ::1, localhost) → always allowed.
///   - Wildcard / all-interfaces (0.0.0.0, ::, *, +) → always rejected; binds every NIC
///     including public ones which the spec forbids unconditionally.
///   - Any other specific host → allowed only when <paramref name="allowNonLoopback"/> is true.
///
/// The intended exposure path is <c>tailscale serve</c> fronting the loopback bind.
/// </summary>
internal static class GatewayBindPolicy
{
    /// <summary>
    /// Validates <paramref name="bindAddress"/> against the bind policy.
    /// </summary>
    /// <param name="bindAddress">The full URL string from <see cref="GatewayOptions.BindAddress"/>.</param>
    /// <param name="allowNonLoopback">Value of <see cref="GatewayOptions.AllowNonLoopbackBind"/>.</param>
    /// <returns>
    /// <c>(true, null)</c> when allowed.
    /// <c>(false, actionableMessage)</c> when rejected — the message is safe to log.
    /// </returns>
    internal static (bool Allowed, string? Error) Validate(string bindAddress, bool allowNonLoopback)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
        {
            return (false,
                "Gateway bind address is empty or missing. " +
                "Set Gateway:BindAddress to a loopback URL such as 'http://127.0.0.1:5500'. " +
                "Use 'tailscale serve' to expose the gateway over Tailscale.");
        }

        if (!Uri.TryCreate(bindAddress, UriKind.Absolute, out Uri? uri))
        {
            return (false,
                $"Gateway bind address '{bindAddress}' is not a valid URL. " +
                "Set Gateway:BindAddress to a loopback URL such as 'http://127.0.0.1:5500'. " +
                "Use 'tailscale serve' to expose the gateway over Tailscale.");
        }

        string host = uri.Host;

        if (IsWildcard(host))
        {
            return (false,
                $"Gateway bind address '{bindAddress}' uses a wildcard/all-interfaces host ('{host}'). " +
                "Binding all interfaces exposes public NICs, which the gateway security policy forbids. " +
                "Bind to loopback ('http://127.0.0.1:5500') and use 'tailscale serve' to expose " +
                "the gateway over your Tailscale network instead.");
        }

        if (IsLoopback(host))
        {
            return (true, null);
        }

        // Specific non-loopback address.
        if (!allowNonLoopback)
        {
            return (false,
                $"Gateway bind address '{bindAddress}' is not a loopback address. " +
                "The intended exposure path is 'tailscale serve' fronting the loopback bind " +
                "('http://127.0.0.1:5500'), not a direct non-loopback bind. " +
                "Set Gateway:AllowNonLoopbackBind=true to override, or bind to loopback " +
                "and use 'tailscale serve' instead.");
        }

        // Allowed non-loopback with explicit opt-in — caller should log a warning.
        return (true, null);
    }

    /// <summary>
    /// Returns true when the host resolves to a loopback address.
    /// Handles IPv4 loopback (127.x.x.x), IPv6 loopback (::1), and the hostname "localhost".
    /// Strips IPv6 brackets produced by <see cref="Uri.Host"/> (e.g. "[::1]" → "::1").
    /// </summary>
    internal static bool IsLoopback(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string bare = StripBrackets(host);

        return IPAddress.TryParse(bare, out IPAddress? addr) && IPAddress.IsLoopback(addr);
    }

    /// <summary>
    /// Returns true when the host is a wildcard / all-interfaces specifier.
    /// ASP.NET Core accepts "*" and "+" as wildcard hostnames in addition to
    /// the standard IP wildcards 0.0.0.0 and ::.
    /// </summary>
    internal static bool IsWildcard(string host)
    {
        if (host is "*" or "+")
        {
            return true;
        }

        string bare = StripBrackets(host);

        if (!IPAddress.TryParse(bare, out IPAddress? addr))
        {
            return false;
        }

        return addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any);
    }

    /// <summary>Returns true when the bind is a specific non-loopback address and the opt-in flag is set.</summary>
    internal static bool IsNonLoopbackWithOptIn(string bindAddress, bool allowNonLoopback)
    {
        if (!Uri.TryCreate(bindAddress, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string host = uri.Host;
        return !IsLoopback(host) && !IsWildcard(host) && allowNonLoopback;
    }

    private static string StripBrackets(string host)
    {
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            return host[1..^1];
        }

        return host;
    }
}
