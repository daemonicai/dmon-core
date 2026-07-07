using System.Net;

namespace Dmail;

/// <summary>
/// Validates the Dmail HTTP bind address against the loopback-by-default policy (D1).
///
/// Rules:
///   - Loopback (127.x.x.x, ::1, localhost) → always allowed.
///   - Wildcard / all-interfaces (0.0.0.0, ::, *, +) → allowed only with the
///     <c>DMAIL_ALLOW_NONLOOPBACK</c> opt-in.
///   - Any other specific host → allowed only with the same opt-in.
///
/// This is a local ~30-line copy of the rule shape in
/// <c>frontends/Dmon.Network/NetworkBindPolicy.cs</c>, which remains the source of
/// truth. It is not referenced directly: it is <c>internal</c> to a
/// <c>frontends/</c> project, and a <c>services/ → frontends/</c> reference would
/// invert the ADR-028 layering. Promote to a shared package if a third consumer
/// needs this rule.
/// </summary>
internal static class BindAddressPolicy
{
    /// <summary>
    /// Resolves the effective bind address from configuration, validating it against
    /// the loopback policy. Throws <see cref="InvalidOperationException"/> on rejection.
    /// </summary>
    /// <param name="bindAddress">The raw <c>DMAIL_BIND_ADDRESS</c> value, or null/empty if unset.</param>
    /// <param name="port">The <c>DMAIL_PORT</c> value, used when <paramref name="bindAddress"/> is unset.</param>
    /// <param name="allowNonLoopback">The <c>DMAIL_ALLOW_NONLOOPBACK</c> value.</param>
    internal static string Resolve(string? bindAddress, string port, bool allowNonLoopback)
    {
        string resolved = string.IsNullOrWhiteSpace(bindAddress)
            ? $"http://127.0.0.1:{port}"
            : bindAddress;

        (bool allowed, string? error) = Validate(resolved, allowNonLoopback);
        if (!allowed)
        {
            throw new InvalidOperationException(error);
        }

        return resolved;
    }

    internal static (bool Allowed, string? Error) Validate(string bindAddress, bool allowNonLoopback)
    {
        // Kestrel accepts "*" and "+" as wildcard hosts, but System.Uri cannot parse
        // them (Uri.TryCreate returns false for "http://+:8080"), so the host is
        // extracted manually rather than via Uri.Host as NetworkBindPolicy does.
        if (!TryExtractHost(bindAddress, out string? host))
        {
            return (false,
                $"DMAIL_BIND_ADDRESS '{bindAddress}' is not a valid URL. " +
                "Bind loopback (e.g. 'http://127.0.0.1:8080') or set DMAIL_ALLOW_NONLOOPBACK=true.");
        }

        if (IsLoopback(host))
        {
            return (true, null);
        }

        if (!allowNonLoopback)
        {
            string reason = IsWildcard(host)
                ? $"DMAIL_BIND_ADDRESS '{bindAddress}' uses a wildcard/all-interfaces host ('{host}')"
                : $"DMAIL_BIND_ADDRESS '{bindAddress}' is not a loopback address";

            return (false,
                $"{reason}. Bind loopback (e.g. 'http://127.0.0.1:8080') and put Dmail behind Tailscale, " +
                "or set DMAIL_ALLOW_NONLOOPBACK=true to override.");
        }

        return (true, null);
    }

    private static bool IsLoopback(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string bare = StripBrackets(host);

        return IPAddress.TryParse(bare, out IPAddress? addr) && IPAddress.IsLoopback(addr);
    }

    private static bool IsWildcard(string host)
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

    private static string StripBrackets(string host)
    {
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            return host[1..^1];
        }

        return host;
    }

    private static bool TryExtractHost(string bindAddress, out string host)
    {
        host = string.Empty;

        int schemeEnd = bindAddress.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return false;
        }

        string rest = bindAddress[(schemeEnd + 3)..];
        int pathStart = rest.IndexOf('/');
        string authority = pathStart >= 0 ? rest[..pathStart] : rest;

        if (authority.Length == 0)
        {
            return false;
        }

        if (authority[0] == '[')
        {
            int closingBracket = authority.IndexOf(']');
            if (closingBracket < 0)
            {
                return false;
            }

            host = authority[..(closingBracket + 1)];
            return true;
        }

        int lastColon = authority.LastIndexOf(':');
        if (lastColon > 0)
        {
            string portCandidate = authority[(lastColon + 1)..];
            if (portCandidate.Length > 0 && portCandidate.All(char.IsDigit))
            {
                host = authority[..lastColon];
                return true;
            }
        }

        host = authority;
        return true;
    }
}
