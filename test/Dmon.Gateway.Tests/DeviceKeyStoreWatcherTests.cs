using System.Collections.Immutable;
using Dmon.Gateway.DeviceKeys;
using Dmon.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Group 4 — hot-reload of the device-key set (tasks 4.1–4.3).
///
/// Tests drive <c>DeviceKeyStoreWatcher.Reload()</c> directly via a helper that
/// creates the watcher with a temp-directory path. This avoids any dependency on
/// real <see cref="FileSystemWatcher"/> event timing.
/// </summary>
public sealed class DeviceKeyStoreWatcherTests : IDisposable
{
    private readonly string _tempDir;

    public DeviceKeyStoreWatcherTests()
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

    /// <summary>
    /// Creates a watcher (not started) whose <c>Reload()</c> can be called directly.
    /// </summary>
    private (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) CreateWatcher(
        DeviceKeySet? initial = null,
        DeviceConnectionIndex? index = null)
    {
        DeviceKeySetProvider provider = new(initial ?? DeviceKeySet.Empty);
        GatewayDeviceKeyPaths paths = new(
            DevicesPath: DevicesPath,
            LastSeenPath: Path.Combine(_tempDir, "lastseen.json"));

        DeviceKeyStoreWatcher watcher = new(
            provider,
            index ?? new DeviceConnectionIndex(),
            paths,
            NullLogger<DeviceKeyStoreWatcher>.Instance);

        return (watcher, provider);
    }

    private static string OneDeviceJson(string keyId = "k1", string secretHash = "aabbcc") => $$"""
        {
          "schemaVersion": 1,
          "devices": [
            {
              "keyId": "{{keyId}}",
              "name": "test-device",
              "secretHash": "{{secretHash}}",
              "createdAt": "2024-01-01T00:00:00Z"
            }
          ]
        }
        """;

    private static DeviceKeySet GoodSet(string keyId = "k1")
    {
        return new DeviceKeySet(
            ImmutableArray.Create(
                new DeviceCredential(
                    keyId,
                    "test-device",
                    "aabbcc",
                    DateTimeOffset.UtcNow,
                    RevokedAt: null)));
    }

    // -------------------------------------------------------------------------
    // 4.3 — pairing append takes effect with no restart
    // -------------------------------------------------------------------------

    [Fact]
    public void Reload_NewEntryAppended_CurrentUpdatedToNewSet()
    {
        // Arrange: start with Empty (absent file)
        (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) = CreateWatcher(DeviceKeySet.Empty);
        File.WriteAllText(DevicesPath, OneDeviceJson("k-new"));

        // Act
        watcher.Reload();

        // Assert: provider now knows the new entry
        Assert.False(provider.Current.IsEmpty);
        Assert.Single(provider.Current.Entries);
        Assert.Equal("k-new", provider.Current.Entries[0].KeyId);
    }

    [Fact]
    public void Reload_SecondEntryAdded_BothEntriesPresent()
    {
        // Arrange: seed provider with a good set (k1 already active)
        (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) = CreateWatcher(GoodSet("k1"));

        // Update file with two entries
        string json = """
            {
              "schemaVersion": 1,
              "devices": [
                { "keyId": "k1", "name": "phone",  "secretHash": "aabbcc", "createdAt": "2024-01-01T00:00:00Z" },
                { "keyId": "k2", "name": "laptop", "secretHash": "ddeeff", "createdAt": "2024-01-02T00:00:00Z" }
              ]
            }
            """;
        File.WriteAllText(DevicesPath, json);

        // Act
        watcher.Reload();

        // Assert
        Assert.Equal(2, provider.Current.Entries.Count);
        Assert.Contains(provider.Current.Entries, e => e.KeyId == "k1");
        Assert.Contains(provider.Current.Entries, e => e.KeyId == "k2");
    }

    // -------------------------------------------------------------------------
    // 4.3 — malformed-after-good keeps last-good (fail-closed on parse error)
    // -------------------------------------------------------------------------

    [Fact]
    public void Reload_MalformedContent_RetainsLastGood()
    {
        // Arrange: seed with a good non-empty set
        DeviceKeySet goodSet = GoodSet("k-good");
        (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) = CreateWatcher(goodSet);
        File.WriteAllText(DevicesPath, "{ BROKEN JSON ]]]]");

        // Act
        watcher.Reload();

        // Assert: Current is unchanged — same entries, same non-empty state
        Assert.False(provider.Current.IsEmpty);
        Assert.Single(provider.Current.Entries);
        Assert.Equal("k-good", provider.Current.Entries[0].KeyId);
    }

    [Fact]
    public void Reload_WrongSchemaVersion_RetainsLastGood()
    {
        DeviceKeySet goodSet = GoodSet("k-good");
        (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) = CreateWatcher(goodSet);
        File.WriteAllText(DevicesPath, """{"schemaVersion": 99, "devices": []}""");

        watcher.Reload();

        Assert.False(provider.Current.IsEmpty);
        Assert.Equal("k-good", provider.Current.Entries[0].KeyId);
    }

    // -------------------------------------------------------------------------
    // 4.3 — absent-at-runtime keeps last-good (the fail-open guard)
    // -------------------------------------------------------------------------

    [Fact]
    public void Reload_FileAbsentAtRuntime_RetainsLastGood()
    {
        // This is the key fail-closed test: a good set is loaded at startup,
        // then the file is deleted (e.g. mid-atomic-replace). Reload must NOT
        // fail open by swapping to the Empty/disabled state.

        DeviceKeySet goodSet = GoodSet("k-persistent");
        (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) = CreateWatcher(goodSet);

        // Confirm the file does not exist
        Assert.False(File.Exists(DevicesPath));

        // Act
        watcher.Reload();

        // Assert: still the good set, NOT Empty
        Assert.False(provider.Current.IsEmpty);
        Assert.Equal("k-persistent", provider.Current.Entries[0].KeyId);
    }

    [Fact]
    public void Reload_FileDeletedAfterGoodLoad_RetainsLastGoodNotEmpty()
    {
        // Arrange: write a good file, reload to load it, then delete the file.
        (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) = CreateWatcher(DeviceKeySet.Empty);
        File.WriteAllText(DevicesPath, OneDeviceJson("k1"));
        watcher.Reload();
        Assert.False(provider.Current.IsEmpty); // baseline: loaded ok

        File.Delete(DevicesPath);

        // Act: reload with file absent
        watcher.Reload();

        // Assert: last-good retained, not Empty
        Assert.False(provider.Current.IsEmpty);
        Assert.Equal("k1", provider.Current.Entries[0].KeyId);
    }

    // -------------------------------------------------------------------------
    // 4.3 — present-empty array swaps to disabled (distinct from absent)
    // -------------------------------------------------------------------------

    [Fact]
    public void Reload_PresentEmptyDevicesArray_SwapsToEmpty()
    {
        // An explicit empty array is the operator's choice to disable auth.
        // Reload must swap Current to the empty set (not keep last-good).

        DeviceKeySet goodSet = GoodSet("k1");
        (DeviceKeyStoreWatcher watcher, DeviceKeySetProvider provider) = CreateWatcher(goodSet);
        File.WriteAllText(DevicesPath, """{"schemaVersion":1,"devices":[]}""");

        watcher.Reload();

        Assert.True(provider.Current.IsEmpty);
    }

    // -------------------------------------------------------------------------
    // 4.3 — absent-at-startup disables auth (re-assert startup behaviour)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_AbsentFile_ReturnsEmpty_StartupContract()
    {
        // Re-asserts the startup contract (group 1 / group 3 path):
        // absent file → DeviceKeySet.Empty → auth disabled.
        string nonexistent = Path.Combine(_tempDir, "sub", "devices.json");

        DeviceKeySet result = await DeviceKeyStoreReader.ReadAsync(nonexistent);

        Assert.True(result.IsEmpty);
        Assert.Same(DeviceKeySet.Empty, result);
    }

    // -------------------------------------------------------------------------
    // Watcher infrastructure: StartAsync creates the directory if absent
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_DirectoryAbsent_CreatesIt()
    {
        string subDir = Path.Combine(_tempDir, "newly-created");
        DeviceKeySetProvider provider = new(DeviceKeySet.Empty);
        GatewayDeviceKeyPaths paths = new(
            DevicesPath: Path.Combine(subDir, "devices.json"),
            LastSeenPath: Path.Combine(subDir, "lastseen.json"));
        DeviceKeyStoreWatcher watcher = new(
            provider, new DeviceConnectionIndex(), paths, NullLogger<DeviceKeyStoreWatcher>.Instance);

        await watcher.StartAsync(CancellationToken.None);
        watcher.Dispose();

        Assert.True(Directory.Exists(subDir));
    }
}
