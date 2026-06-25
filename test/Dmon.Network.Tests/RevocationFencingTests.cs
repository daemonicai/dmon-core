using System.Security.Cryptography;
using System.Text;
using Dmon.Network.DeviceKeys;
using Dmon.Network.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Network.Tests;

/// <summary>
/// Group 6 — revocation fences live connections (tasks 6.1–6.2).
///
/// Tests drive <c>DeviceKeyStoreWatcher.Reload()</c> directly with a shared
/// <see cref="DeviceConnectionIndex"/> to verify that connections whose keyId left
/// the active set are aborted, and that unrelated connections are unaffected.
/// </summary>
public sealed class RevocationFencingTests : IDisposable
{
    private readonly string _tempDir;

    public RevocationFencingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private string DevicesPath => Path.Combine(_tempDir, "devices.json");

    /// <summary>Returns the lowercase-hex SHA-256 of <paramref name="token"/> (the stored form).</summary>
    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// Creates a watcher configured with a shared <paramref name="index"/>.
    /// </summary>
    private (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) CreateWatcher(
        DeviceKeySet initial,
        DeviceConnectionIndex index)
    {
        DeviceKeySetProvider provider = new(initial);
        NetworkDeviceKeyPaths paths = new(
            DevicesPath: DevicesPath,
            LastSeenPath: Path.Combine(_tempDir, "lastseen.json"));

        DeviceKeyStoreWatcher watcher = new(
            provider,
            index,
            paths,
            NullLogger<DeviceKeyStoreWatcher>.Instance);

        return (watcher, provider);
    }

    /// <summary>
    /// Builds a <see cref="DeviceKeySet"/> with a single active entry for <paramref name="keyId"/>
    /// authenticated by <paramref name="token"/>.
    /// </summary>
    private static DeviceKeySet MakeSet(string keyId, string token) =>
        DeviceKeyStoreReader.Parse(OneDeviceJson(keyId, HashToken(token)));

    /// <summary>
    /// JSON for a single active device with arbitrary secretHash.
    /// </summary>
    private static string OneDeviceJson(string keyId, string secretHash) => $$"""
        {
          "schemaVersion": 1,
          "devices": [
            {
              "keyId": "{{keyId}}",
              "name": "{{keyId}}-device",
              "secretHash": "{{secretHash}}",
              "createdAt": "2024-01-01T00:00:00Z"
            }
          ]
        }
        """;

    /// <summary>
    /// JSON for two active devices.
    /// </summary>
    private static string TwoDevicesJson(
        string keyId1, string hash1,
        string keyId2, string hash2) => $$"""
        {
          "schemaVersion": 1,
          "devices": [
            {
              "keyId": "{{keyId1}}",
              "name": "{{keyId1}}-device",
              "secretHash": "{{hash1}}",
              "createdAt": "2024-01-01T00:00:00Z"
            },
            {
              "keyId": "{{keyId2}}",
              "name": "{{keyId2}}-device",
              "secretHash": "{{hash2}}",
              "createdAt": "2024-01-02T00:00:00Z"
            }
          ]
        }
        """;

    // -------------------------------------------------------------------------
    // 6.1/6.2 — revocation fences all that key's connections, across sessions
    // -------------------------------------------------------------------------

    [Fact]
    public void Reload_RevokedKey_FencesAllConnectionsForThatKey_AcrossSessions()
    {
        // Arrange: shared index has two connections under k1 (simulating two sessions)
        // and one connection under k2. Initial active set is {k1, k2}.
        const string token1 = "secret-k1";
        const string token2 = "secret-k2";

        DeviceConnectionIndex index = new();
        AbortRecordingConnection k1ConnA = new("k1");
        AbortRecordingConnection k1ConnB = new("k1");
        AbortRecordingConnection k2Conn = new("k2");

        index.Add("k1", k1ConnA);
        index.Add("k1", k1ConnB);
        index.Add("k2", k2Conn);

        DeviceKeySet initial = DeviceKeyStoreReader.Parse(
            TwoDevicesJson("k1", HashToken(token1), "k2", HashToken(token2)));

        (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) = CreateWatcher(initial, index);

        // Write a file that revokes k1 (revokedAt set) — k2 survives.
        string revokedJson = $$"""
            {
              "schemaVersion": 1,
              "devices": [
                {
                  "keyId": "k1",
                  "name": "k1-device",
                  "secretHash": "{{HashToken(token1)}}",
                  "createdAt": "2024-01-01T00:00:00Z",
                  "revokedAt": "2024-06-01T00:00:00Z"
                },
                {
                  "keyId": "k2",
                  "name": "k2-device",
                  "secretHash": "{{HashToken(token2)}}",
                  "createdAt": "2024-01-02T00:00:00Z"
                }
              ]
            }
            """;
        File.WriteAllText(DevicesPath, revokedJson);

        // Act
        watcher.Reload();

        // Assert — both k1 connections aborted.
        Assert.True(k1ConnA.AbortCalled, "k1 connection A must be aborted on revocation");
        Assert.True(k1ConnB.AbortCalled, "k1 connection B must be aborted on revocation");

        // Assert — k2 connection unaffected.
        Assert.False(k2Conn.AbortCalled, "k2 connection must NOT be aborted — unrelated key");

        // Assert — subsequent upgrade attempt with k1's token is rejected (401 path).
        DeviceAuthResult k1Auth = DeviceKeyAuthenticator.Authenticate(
            authorizationHeader: $"Bearer {token1}",
            keySet: provider.Current);
        Assert.False(k1Auth.Authorized, "k1 token must be rejected after revocation");

        // Assert — k2's token is still authorized.
        DeviceAuthResult k2Auth = DeviceKeyAuthenticator.Authenticate(
            authorizationHeader: $"Bearer {token2}",
            keySet: provider.Current);
        Assert.True(k2Auth.Authorized, "k2 token must still be authorized");
        Assert.Equal("k2", k2Auth.KeyId);
    }

    // -------------------------------------------------------------------------
    // 6.2 — row-deletion (not revokedAt) also fences
    // -------------------------------------------------------------------------

    [Fact]
    public void Reload_DeletedKey_FencesConnections()
    {
        // Proves the diff is "left the active set", not only "revokedAt set".
        const string token1 = "secret-k1";
        const string token2 = "secret-k2";

        DeviceConnectionIndex index = new();
        AbortRecordingConnection k1Conn = new("k1");
        AbortRecordingConnection k2Conn = new("k2");
        index.Add("k1", k1Conn);
        index.Add("k2", k2Conn);

        DeviceKeySet initial = DeviceKeyStoreReader.Parse(
            TwoDevicesJson("k1", HashToken(token1), "k2", HashToken(token2)));

        (DeviceKeyStoreWatcher watcher, _) = CreateWatcher(initial, index);

        // New file simply omits k1 — no revokedAt, just gone.
        File.WriteAllText(DevicesPath, OneDeviceJson("k2", HashToken(token2)));

        watcher.Reload();

        Assert.True(k1Conn.AbortCalled, "k1 connection must be aborted when its row is deleted");
        Assert.False(k2Conn.AbortCalled, "k2 connection must not be aborted");
    }

    // -------------------------------------------------------------------------
    // 6.2 — rename (same keyId, different name) does NOT fence
    // -------------------------------------------------------------------------

    [Fact]
    public void Reload_RenamedKey_DoesNotFenceConnections()
    {
        // A device rename keeps the same keyId in the active set → not in the diff → no fence.
        const string token1 = "secret-k1";

        DeviceConnectionIndex index = new();
        AbortRecordingConnection k1Conn = new("k1");
        index.Add("k1", k1Conn);

        DeviceKeySet initial = MakeSet("k1", token1);
        (DeviceKeyStoreWatcher watcher, _) = CreateWatcher(initial, index);

        // New file: same keyId+secretHash, different name.
        string renamedJson = $$"""
            {
              "schemaVersion": 1,
              "devices": [
                {
                  "keyId": "k1",
                  "name": "renamed-device",
                  "secretHash": "{{HashToken(token1)}}",
                  "createdAt": "2024-01-01T00:00:00Z"
                }
              ]
            }
            """;
        File.WriteAllText(DevicesPath, renamedJson);

        watcher.Reload();

        Assert.False(k1Conn.AbortCalled, "rename must NOT fence the connection — keyId unchanged");
    }

    // -------------------------------------------------------------------------
    // 6.2 — keep-last-good (malformed file) does NOT fence
    // -------------------------------------------------------------------------

    [Fact]
    public void Reload_MalformedFile_DoesNotFenceConnections()
    {
        // On parse failure the provider is not swapped, so no diff and no fencing.
        const string token1 = "secret-k1";

        DeviceConnectionIndex index = new();
        AbortRecordingConnection k1Conn = new("k1");
        index.Add("k1", k1Conn);

        DeviceKeySet initial = MakeSet("k1", token1);
        (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) = CreateWatcher(initial, index);

        File.WriteAllText(DevicesPath, "{ NOT VALID JSON ]]]]");

        watcher.Reload();

        Assert.False(k1Conn.AbortCalled, "malformed file must not trigger fencing — no swap occurred");

        // Provider is unchanged (last-good retained).
        DeviceAuthResult auth = DeviceKeyAuthenticator.Authenticate(
            authorizationHeader: $"Bearer {token1}",
            keySet: provider.Current);
        Assert.True(auth.Authorized, "k1 token must still be authorized after malformed reload");
    }

    // -------------------------------------------------------------------------
    // 6.2 — empty transition fences all previously-active connections
    // -------------------------------------------------------------------------

    [Fact]
    public void Reload_EmptyDevicesArray_FencesAllActiveConnections()
    {
        // Operator explicitly empties devices.json → all previously-active keyIds are in the diff.
        const string token1 = "secret-k1";

        DeviceConnectionIndex index = new();
        AbortRecordingConnection k1Conn = new("k1");
        index.Add("k1", k1Conn);

        DeviceKeySet initial = MakeSet("k1", token1);
        (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) = CreateWatcher(initial, index);

        File.WriteAllText(DevicesPath, """{"schemaVersion":1,"devices":[]}""");

        watcher.Reload();

        Assert.True(k1Conn.AbortCalled, "k1 connection must be fenced when operator empties devices.json");
        Assert.True(provider.Current.IsEmpty, "provider must reflect the empty set after reload");
    }

    // -------------------------------------------------------------------------
    // Fake connection — records Abort() calls
    // -------------------------------------------------------------------------

    /// <summary>
    /// Minimal <see cref="INetworkConnection"/> that records whether <see cref="Abort"/> was called.
    /// </summary>
    private sealed class AbortRecordingConnection(string keyId) : INetworkConnection
    {
        public string? KeyId { get; } = keyId;

        public bool AbortCalled { get; private set; }

        public void Abort() => AbortCalled = true;

        public ValueTask SendAsync(string frame, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
