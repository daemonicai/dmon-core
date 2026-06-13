using System.Security.Cryptography;
using System.Text;

namespace Dmon.Gateway.DeviceKeys;

/// <summary>
/// Validates an <c>Authorization: Bearer &lt;token&gt;</c> header against the active device-key set.
///
/// When the set is empty, auth is disabled and every request is authorized (no keyId).
/// When the set is non-empty, the presented token is SHA-256-hashed and compared
/// constant-time against every active entry's stored hash; a matching active entry authorizes the upgrade.
/// </summary>
internal static class DeviceKeyAuthenticator
{
    private const string BearerScheme = "Bearer";

    /// <summary>
    /// Evaluates the authorization header against the active device-key set.
    /// </summary>
    /// <param name="authorizationHeader">
    /// Value of the <c>Authorization</c> request header, or <see langword="null"/> if absent.
    /// </param>
    /// <param name="keySet">The active device-key snapshot.</param>
    /// <returns>
    /// A <see cref="DeviceAuthResult"/> whose <c>Authorized</c> flag indicates whether the
    /// request is permitted, and whose <c>KeyId</c> carries the matched key identifier (or
    /// <see langword="null"/> when authorized via the empty-set short-circuit).
    /// </returns>
    internal static DeviceAuthResult Authenticate(string? authorizationHeader, DeviceKeySet keySet)
    {
        // Empty set → auth disabled; authorize without a keyId.
        if (keySet.IsEmpty)
        {
            return DeviceAuthResult.AuthorizedNoKey;
        }

        // Key set is non-empty — header must be present and well-formed.
        if (string.IsNullOrEmpty(authorizationHeader))
        {
            return DeviceAuthResult.NotAuthorized;
        }

        // Parse "Bearer <token>" — scheme comparison is case-insensitive (RFC 7235 §5.1).
        int spaceIndex = authorizationHeader.IndexOf(' ');
        if (spaceIndex <= 0)
        {
            return DeviceAuthResult.NotAuthorized;
        }

        ReadOnlySpan<char> scheme = authorizationHeader.AsSpan(0, spaceIndex);
        if (!scheme.Equals(BearerScheme.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return DeviceAuthResult.NotAuthorized;
        }

        ReadOnlySpan<char> token = authorizationHeader.AsSpan(spaceIndex + 1);
        if (token.IsEmpty)
        {
            return DeviceAuthResult.NotAuthorized;
        }

        // SHA-256 the presented token once; reuse the 32-byte result across all entries.
        byte[] presentedHashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(token.ToString()));

        // Iterate ALL entries — no early-out — to keep comparison time constant across the set.
        string? matchedKeyId = null;
        foreach (DeviceCredential entry in keySet.Entries)
        {
            byte[] entryHashBytes;
            try
            {
                entryHashBytes = Convert.FromHexString(entry.SecretHash);
            }
            catch (FormatException)
            {
                // Malformed hex row: skip without breaking constant-time across other entries.
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(presentedHashBytes, entryHashBytes))
            {
                // Record the match but keep iterating — no early-out (timing side-channel).
                matchedKeyId = entry.KeyId;
            }
        }

        return matchedKeyId is not null
            ? new DeviceAuthResult(Authorized: true, KeyId: matchedKeyId)
            : DeviceAuthResult.NotAuthorized;
    }
}
