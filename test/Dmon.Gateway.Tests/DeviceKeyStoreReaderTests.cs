using Dmon.Gateway.DeviceKeys;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Group 1 — Device-key store model and reader (tasks 1.1–1.4).
/// </summary>
public sealed class DeviceKeyStoreReaderTests
{
    // -------------------------------------------------------------------------
    // 1.4 — valid file: active set excludes revoked entries
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_ValidEnvelope_ActiveEntriesOnly()
    {
        string json = """
            {
              "schemaVersion": 1,
              "devices": [
                {
                  "keyId": "k1",
                  "name": "phone",
                  "secretHash": "aabbcc",
                  "createdAt": "2024-01-01T00:00:00Z"
                },
                {
                  "keyId": "k2",
                  "name": "laptop",
                  "secretHash": "ddeeff",
                  "createdAt": "2024-01-02T00:00:00Z",
                  "revokedAt": "2024-06-01T00:00:00Z"
                }
              ]
            }
            """;

        DeviceKeySet set = DeviceKeyStoreReader.Parse(json);

        Assert.False(set.IsEmpty);
        Assert.Single(set.Entries);
        Assert.Equal("k1", set.Entries[0].KeyId);
        Assert.Equal("phone", set.Entries[0].Name);
        Assert.Equal("aabbcc", set.Entries[0].SecretHash);
        Assert.Null(set.Entries[0].RevokedAt);
    }

    [Fact]
    public void Parse_AllEntriesRevoked_ReturnsEmptySet()
    {
        string json = """
            {
              "schemaVersion": 1,
              "devices": [
                {
                  "keyId": "k1",
                  "name": "old-phone",
                  "secretHash": "aabbcc",
                  "createdAt": "2024-01-01T00:00:00Z",
                  "revokedAt": "2024-06-01T00:00:00Z"
                }
              ]
            }
            """;

        DeviceKeySet set = DeviceKeyStoreReader.Parse(json);

        Assert.True(set.IsEmpty);
        Assert.Empty(set.Entries);
    }

    [Fact]
    public void Parse_MultipleActiveEntries_AllIncluded()
    {
        string json = """
            {
              "schemaVersion": 1,
              "devices": [
                { "keyId": "k1", "name": "a", "secretHash": "aa", "createdAt": "2024-01-01T00:00:00Z" },
                { "keyId": "k2", "name": "b", "secretHash": "bb", "createdAt": "2024-01-02T00:00:00Z" },
                { "keyId": "k3", "name": "c", "secretHash": "cc", "createdAt": "2024-01-03T00:00:00Z", "revokedAt": "2024-06-01T00:00:00Z" }
              ]
            }
            """;

        DeviceKeySet set = DeviceKeyStoreReader.Parse(json);

        Assert.Equal(2, set.Entries.Count);
        Assert.Contains(set.Entries, e => e.KeyId == "k1");
        Assert.Contains(set.Entries, e => e.KeyId == "k2");
        Assert.DoesNotContain(set.Entries, e => e.KeyId == "k3");
    }

    // -------------------------------------------------------------------------
    // 1.4 — empty devices array → empty (disabled) set
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_EmptyDevicesArray_ReturnsEmptySet()
    {
        string json = """{"schemaVersion": 1, "devices": []}""";

        DeviceKeySet set = DeviceKeyStoreReader.Parse(json);

        Assert.True(set.IsEmpty);
    }

    // -------------------------------------------------------------------------
    // 1.4 — absent file → empty (disabled) set
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_AbsentFile_ReturnsEmptySet()
    {
        string nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "devices.json");

        DeviceKeySet set = await DeviceKeyStoreReader.ReadAsync(nonExistentPath);

        Assert.True(set.IsEmpty);
        Assert.Same(DeviceKeySet.Empty, set);
    }

    // -------------------------------------------------------------------------
    // 1.4 — malformed file → parse failure surfaced (not swallowed as empty set)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_NotJson_ThrowsInvalidOperationException()
    {
        string content = "this is not json at all";

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => DeviceKeyStoreReader.Parse(content));

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Parse_ValidJsonWrongShape_ThrowsInvalidOperationException()
    {
        // Valid JSON but not a devices envelope.
        string content = """{"foo": "bar"}""";

        // schemaVersion will be 0 (default int) — unsupported version.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => DeviceKeyStoreReader.Parse(content));

        Assert.Contains("schemaVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UnsupportedSchemaVersion_ThrowsInvalidOperationException()
    {
        string json = """{"schemaVersion": 99, "devices": []}""";

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => DeviceKeyStoreReader.Parse(json));

        Assert.Contains("99", ex.Message);
        Assert.Contains("schemaVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_EmptyString_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(
            () => DeviceKeyStoreReader.Parse(string.Empty));
    }

    [Fact]
    public void Parse_NullJson_ThrowsInvalidOperationException()
    {
        // A literal JSON null would deserialise to null, which is caught.
        Assert.Throws<InvalidOperationException>(
            () => DeviceKeyStoreReader.Parse("null"));
    }

    // -------------------------------------------------------------------------
    // 1.4 — malformed-file failure is NOT the same as absent-file empty set
    //        (the watcher in group 4 depends on this distinction)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_PresentButMalformed_ThrowsNotEmpty()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{ BROKEN JSON");

            // Must throw — must not collapse silently to the empty/disabled set.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => DeviceKeyStoreReader.ReadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // -------------------------------------------------------------------------
    // 1.2 / D7 — unknown fields are ignored (room for future expiresAt)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_UnknownFields_Ignored()
    {
        string json = """
            {
              "schemaVersion": 1,
              "devices": [
                {
                  "keyId": "k1",
                  "name": "phone",
                  "secretHash": "aabbcc",
                  "createdAt": "2024-01-01T00:00:00Z",
                  "expiresAt": "2099-01-01T00:00:00Z",
                  "unknownFutureField": true
                }
              ]
            }
            """;

        DeviceKeySet set = DeviceKeyStoreReader.Parse(json);

        // Must not throw; the active entry is still included.
        Assert.Single(set.Entries);
        Assert.Equal("k1", set.Entries[0].KeyId);
    }

    // -------------------------------------------------------------------------
    // 1.3 — DeviceKeySet.Empty is a stable singleton reference
    // -------------------------------------------------------------------------

    [Fact]
    public void Empty_IsSingletonReference()
    {
        Assert.Same(DeviceKeySet.Empty, DeviceKeySet.Empty);
        Assert.True(DeviceKeySet.Empty.IsEmpty);
    }

    // -------------------------------------------------------------------------
    // Nit 3 — present-but-empty devices array → IsEmpty (not singleton identity)
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_PresentEmptyArray_IsEmpty()
    {
        // A present file with an empty devices array is auth-disabled, same as absent file.
        // The result must satisfy IsEmpty; it need not be the Empty singleton.
        string json = """{"schemaVersion":1,"devices":[]}""";

        DeviceKeySet set = DeviceKeyStoreReader.Parse(json);

        Assert.True(set.IsEmpty);
    }

    // -------------------------------------------------------------------------
    // Nit 4 — entries with blank/missing secretHash are excluded from active set
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_BlankSecretHash_ExcludedFromActiveSet()
    {
        string json = """
            {
              "schemaVersion": 1,
              "devices": [
                { "keyId": "k-blank", "name": "blank-hash", "secretHash": "", "createdAt": "2024-01-01T00:00:00Z" },
                { "keyId": "k-ws", "name": "whitespace-hash", "secretHash": "   ", "createdAt": "2024-01-02T00:00:00Z" },
                { "keyId": "k-missing", "name": "missing-hash", "createdAt": "2024-01-03T00:00:00Z" },
                { "keyId": "k-valid", "name": "good", "secretHash": "aabbcc", "createdAt": "2024-01-04T00:00:00Z" }
              ]
            }
            """;

        DeviceKeySet set = DeviceKeyStoreReader.Parse(json);

        Assert.Single(set.Entries);
        Assert.Equal("k-valid", set.Entries[0].KeyId);
    }
}
