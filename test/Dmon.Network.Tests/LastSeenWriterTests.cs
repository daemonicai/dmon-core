using Dmon.Network.DeviceKeys;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json.Nodes;

namespace Dmon.Network.Tests;

/// <summary>
/// Group 7 — network-host-owned last-seen telemetry writer (tasks 7.1–7.2).
///
/// All tests use a temp directory and <see cref="FakeTimeProvider"/> for deterministic throttle.
/// Endpoint tests remain IO-free via the no-op default in <see cref="NetworkConnectionEndpoint.TestOptions"/>.
/// </summary>
public sealed class LastSeenWriterTests : IDisposable
{
    private readonly string _tempDir;

    public LastSeenWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dmon-lsw-" + Guid.NewGuid().ToString("N"));
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

    private string LastSeenPath => Path.Combine(_tempDir, "lastseen.json");

    private LastSeenWriter CreateWriter(
        FakeTimeProvider timeProvider,
        int throttleSeconds = 60)
    {
        NetworkOptions opts = new() { LastSeenThrottleSeconds = throttleSeconds };
        StaticOptionsMonitor<NetworkOptions> monitor = new(opts);
        return new LastSeenWriter(
            path: LastSeenPath,
            options: monitor,
            timeProvider: timeProvider,
            logger: NullLogger<LastSeenWriter>.Instance);
    }

    private static (int schemaVersion, JsonObject lastSeen) ReadFile(string path)
    {
        string json = File.ReadAllText(path);
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        int schemaVersion = root["schemaVersion"]!.GetValue<int>();
        JsonObject lastSeen = root["lastSeen"]!.AsObject();
        return (schemaVersion, lastSeen);
    }

    // -------------------------------------------------------------------------
    // 7.1 — Writes lastseen.json on first attach
    // -------------------------------------------------------------------------

    [Fact]
    public void RecordAttach_FirstAttach_WritesFile()
    {
        FakeTimeProvider clock = new();
        DateTimeOffset t0 = clock.GetUtcNow();
        LastSeenWriter writer = CreateWriter(clock);

        writer.RecordAttach("k1");

        Assert.True(File.Exists(LastSeenPath));
        (int schemaVersion, JsonObject lastSeen) = ReadFile(LastSeenPath);
        Assert.Equal(1, schemaVersion);
        Assert.True(lastSeen.ContainsKey("k1"));
        DateTimeOffset recorded = DateTimeOffset.Parse(lastSeen["k1"]!.GetValue<string>());
        Assert.Equal(t0, recorded);
    }

    // -------------------------------------------------------------------------
    // 7.2 — Throttle / coalescing
    // -------------------------------------------------------------------------

    [Fact]
    public void RecordAttach_WithinThrottle_Coalesces()
    {
        FakeTimeProvider clock = new();
        DateTimeOffset t0 = clock.GetUtcNow();
        LastSeenWriter writer = CreateWriter(clock, throttleSeconds: 60);

        writer.RecordAttach("k1");

        // Advance by 30 s (within the 60 s throttle window).
        clock.Advance(TimeSpan.FromSeconds(30));
        writer.RecordAttach("k1");

        // File still holds t0, not t0+30s.
        (_, JsonObject lastSeen) = ReadFile(LastSeenPath);
        DateTimeOffset recorded = DateTimeOffset.Parse(lastSeen["k1"]!.GetValue<string>());
        Assert.Equal(t0, recorded);
    }

    [Fact]
    public void RecordAttach_AfterThrottleExpires_Updates()
    {
        FakeTimeProvider clock = new();
        DateTimeOffset t0 = clock.GetUtcNow();
        LastSeenWriter writer = CreateWriter(clock, throttleSeconds: 60);

        writer.RecordAttach("k1");

        // Within window: coalesce.
        clock.Advance(TimeSpan.FromSeconds(30));
        writer.RecordAttach("k1");

        // Past window: should write new timestamp.
        clock.Advance(TimeSpan.FromSeconds(31)); // total +61 s from t0
        DateTimeOffset t1 = clock.GetUtcNow();
        writer.RecordAttach("k1");

        (_, JsonObject lastSeen) = ReadFile(LastSeenPath);
        DateTimeOffset recorded = DateTimeOffset.Parse(lastSeen["k1"]!.GetValue<string>());
        Assert.Equal(t1, recorded);
    }

    // -------------------------------------------------------------------------
    // 7.2 — Multiple keys; startup-load preserves existing entries
    // -------------------------------------------------------------------------

    [Fact]
    public void RecordAttach_MultipleKeys_BothInFile()
    {
        FakeTimeProvider clock = new();
        LastSeenWriter writer = CreateWriter(clock);

        writer.RecordAttach("k1");
        writer.RecordAttach("k2");

        (_, JsonObject lastSeen) = ReadFile(LastSeenPath);
        Assert.True(lastSeen.ContainsKey("k1"));
        Assert.True(lastSeen.ContainsKey("k2"));
    }

    [Fact]
    public void StartupLoad_PreservesExistingKeys_WhenNewKeyAdded()
    {
        FakeTimeProvider clock = new();

        // First writer records k1 and k2.
        LastSeenWriter first = CreateWriter(clock);
        first.RecordAttach("k1");
        clock.Advance(TimeSpan.FromSeconds(1));
        first.RecordAttach("k2");

        // Second writer (simulates process restart) seeds from the existing file.
        LastSeenWriter second = CreateWriter(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        second.RecordAttach("k3");

        (_, JsonObject lastSeen) = ReadFile(LastSeenPath);
        Assert.True(lastSeen.ContainsKey("k1"), "k1 should survive restart");
        Assert.True(lastSeen.ContainsKey("k2"), "k2 should survive restart");
        Assert.True(lastSeen.ContainsKey("k3"), "k3 should be added by second writer");
    }

    // -------------------------------------------------------------------------
    // 7.2 — Write failure is swallowed (never propagates)
    // -------------------------------------------------------------------------

    [Fact]
    public void RecordAttach_WriteFailure_DoesNotThrow()
    {
        FakeTimeProvider clock = new();

        // A path whose parent is a regular file → any write attempt will fail.
        string blocker = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(blocker, "not a directory");
        string badPath = Path.Combine(blocker, "lastseen.json");

        NetworkOptions opts = new() { LastSeenThrottleSeconds = 0 };
        StaticOptionsMonitor<NetworkOptions> monitor = new(opts);

        LastSeenWriter writer = new(
            path: badPath,
            options: monitor,
            timeProvider: clock,
            logger: NullLogger<LastSeenWriter>.Instance);

        // Must not throw.
        writer.RecordAttach("k1");
    }

    // -------------------------------------------------------------------------
    // 7.2 — No-op mode (null/empty path) — no IO, never throws
    // -------------------------------------------------------------------------

    [Fact]
    public void NoOpMode_NullPath_NeverThrowsNeverWritesFiles()
    {
        LastSeenWriter writer = LastSeenWriter.CreateNoOp();
        writer.RecordAttach("k1"); // must complete without exception
        // Verify no file was written anywhere under the temp dir (it won't be — path is null).
        Assert.Empty(Directory.GetFiles(_tempDir, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void NoOpMode_EmptyPath_NeverThrowsNeverWritesFiles()
    {
        FakeTimeProvider clock = new();
        NetworkOptions opts = new();
        StaticOptionsMonitor<NetworkOptions> monitor = new(opts);

        LastSeenWriter writer = new(
            path: string.Empty,
            options: monitor,
            timeProvider: clock,
            logger: NullLogger<LastSeenWriter>.Instance);

        writer.RecordAttach("k1");
        Assert.Empty(Directory.GetFiles(_tempDir, "*.json", SearchOption.AllDirectories));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Minimal <see cref="IOptionsMonitor{T}"/> backed by a fixed value.</summary>
    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        internal StaticOptionsMonitor(T value) => _value = value;

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
