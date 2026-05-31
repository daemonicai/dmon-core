using System.Security.Cryptography;
using System.Text;

namespace Dmon.Gateway;

/// <summary>
/// Validates the <c>Authorization: Bearer &lt;key&gt;</c> header against the configured shared key.
///
/// When no shared key is configured the check is disabled and every request is authorized.
/// When a shared key is configured, the header must be present, the scheme must be "Bearer"
/// (case-insensitive per RFC 7235), and the token must match the configured key using a
/// constant-time comparison to prevent timing attacks.
/// </summary>
internal static class SharedKeyAuthenticator
{
    private const string BearerScheme = "Bearer";

    /// <summary>
    /// Returns <see langword="true"/> when the request is authorized.
    /// </summary>
    /// <param name="authorizationHeader">
    /// Value of the <c>Authorization</c> request header, or <see langword="null"/> if absent.
    /// </param>
    /// <param name="sharedKey">
    /// The configured shared key from <see cref="GatewayOptions.SharedKey"/>,
    /// or <see langword="null"/> / empty when the check is disabled.
    /// </param>
    internal static bool IsAuthorized(string? authorizationHeader, string? sharedKey)
    {
        // No key configured — check disabled.
        if (string.IsNullOrWhiteSpace(sharedKey))
        {
            return true;
        }

        // Key configured — header must be present and well-formed.
        if (string.IsNullOrEmpty(authorizationHeader))
        {
            return false;
        }

        // Parse "Bearer <token>" — scheme comparison is case-insensitive (RFC 7235 §5.1).
        int spaceIndex = authorizationHeader.IndexOf(' ');
        if (spaceIndex <= 0)
        {
            // No space → bare token or empty scheme; malformed.
            return false;
        }

        ReadOnlySpan<char> scheme = authorizationHeader.AsSpan(0, spaceIndex);
        if (!scheme.Equals(BearerScheme.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> token = authorizationHeader.AsSpan(spaceIndex + 1);
        if (token.IsEmpty)
        {
            return false;
        }

        // Constant-time comparison over UTF-8 bytes to prevent timing side-channels.
        byte[] presentedBytes = Encoding.UTF8.GetBytes(token.ToString());
        byte[] configuredBytes = Encoding.UTF8.GetBytes(sharedKey);

        return CryptographicOperations.FixedTimeEquals(presentedBytes, configuredBytes);
    }
}
