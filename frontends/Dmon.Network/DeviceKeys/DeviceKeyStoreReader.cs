using System.Collections.Immutable;
using System.Text.Json;

namespace Dmon.Network.DeviceKeys;

/// <summary>
/// Reads <c>devices.json</c> and produces an immutable <see cref="DeviceKeySet"/> snapshot.
///
/// Contract:
/// - Absent file → <see cref="DeviceKeySet.Empty"/> (auth disabled; startup default).
/// - Present file → parse and return the active (non-revoked) set.
/// - Malformed or unreadable file → throws <see cref="InvalidOperationException"/> wrapping
///   the underlying cause. The caller (group-4 watcher) must distinguish this from the
///   absent-file case to implement fail-closed-to-last-good behaviour.
/// </summary>
internal static class DeviceKeyStoreReader
{
    // STJ ignores unknown fields by default — unknown future fields (e.g. expiresAt) do not break parse.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // devices.json is machine-written by dmonium with exact casing; strict matching is intentional.
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads <paramref name="path"/> and returns the active-device snapshot.
    /// Returns <see cref="DeviceKeySet.Empty"/> when the file does not exist.
    /// Throws <see cref="InvalidOperationException"/> on any read or parse failure.
    /// </summary>
    public static async Task<DeviceKeySet> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return DeviceKeySet.Empty;
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to read device key store at '{path}'.", ex);
        }

        return Parse(content);
    }

    /// <summary>
    /// Parses JSON content and returns the active-device snapshot.
    /// Throws <see cref="InvalidOperationException"/> when the content is malformed or
    /// the schema version is unsupported.
    /// </summary>
    public static DeviceKeySet Parse(string content)
    {
        DevicesFileEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DevicesFileEnvelope>(content, JsonOptions)
                ?? throw new InvalidOperationException("devices.json deserialised to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "devices.json is not valid JSON or does not match the expected schema.", ex);
        }

        if (envelope.SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported devices.json schemaVersion {envelope.SchemaVersion}; expected 1.");
        }

        ImmutableArray<DeviceCredential>.Builder active = ImmutableArray.CreateBuilder<DeviceCredential>();
        foreach (DeviceEntryDto dto in envelope.Devices)
        {
            // Skip revoked entries and entries with a missing/blank secretHash — an empty hash
            // would match any constant-time comparison and must not enter the active set.
            if (dto.RevokedAt is null && !string.IsNullOrWhiteSpace(dto.SecretHash))
            {
                active.Add(new DeviceCredential(
                    dto.KeyId,
                    dto.Name,
                    dto.SecretHash,
                    dto.CreatedAt,
                    RevokedAt: null));
            }
        }

        return new DeviceKeySet(active.ToImmutable());
    }
}
