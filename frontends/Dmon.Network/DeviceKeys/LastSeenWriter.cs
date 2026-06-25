using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dmon.Network.DeviceKeys;

/// <summary>
/// Gateway-owned writer for <c>lastseen.json</c>. Records the UTC timestamp of the most recent
/// successful attach per device key. The gateway is the sole writer; <c>dmonium</c> only reads.
///
/// Telemetry constraints (binding design decisions):
///   - The recorded timestamp doubles as the throttle gate: one map, no separate dict.
///     Throttle is consistent across restarts because the map is seeded from the file at startup.
///   - Best-effort only: any write failure is caught, logged, and NEVER propagated.
///     A telemetry failure must never fail or block an attach.
///   - No-op when constructed with a null or empty path (test default, no IO, never throws).
/// </summary>
internal sealed class LastSeenWriter
{
    private readonly string? _path;
    private readonly IOptionsMonitor<NetworkOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LastSeenWriter> _logger;

    // Keyed by keyId; value is the timestamp last persisted for that key.
    // Seeded at construction from any existing lastseen.json so the throttle is restart-consistent.
    // Guarded by _lock for both reads and writes.
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    internal LastSeenWriter(
        NetworkDeviceKeyPaths paths,
        IOptionsMonitor<NetworkOptions> options,
        TimeProvider timeProvider,
        ILogger<LastSeenWriter> logger)
        : this(paths.LastSeenPath, options, timeProvider, logger)
    {
    }

    /// <summary>
    /// Returns a no-op instance (null path): <see cref="RecordAttach"/> is a no-op, never touches
    /// disk. Use as the default in <c>TestOptions</c> so endpoint tests that happen to authenticate
    /// a real token (non-null keyId) remain IO-free.
    /// </summary>
    internal static LastSeenWriter CreateNoOp() =>
        new(path: null, options: new StaticOptions(new NetworkOptions()), timeProvider: TimeProvider.System,
            logger: NullLogger<LastSeenWriter>.Instance);

    /// <summary>
    /// Direct-path constructor for tests: accepts a path string (null/empty → no-op mode).
    /// </summary>
    internal LastSeenWriter(
        string? path,
        IOptionsMonitor<NetworkOptions> options,
        TimeProvider timeProvider,
        ILogger<LastSeenWriter> logger)
    {
        _path = path;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;

        if (!string.IsNullOrEmpty(_path))
        {
            LoadExisting();
        }
    }

    /// <summary>
    /// Records that the device identified by <paramref name="keyId"/> successfully attached right
    /// now. If the last recorded attach for this key is within the configured throttle window the
    /// call is a no-op (coalesced). On any write failure the exception is caught and logged;
    /// the attach path is never affected.
    /// </summary>
    internal void RecordAttach(string keyId)
    {
        // No-op mode: path is null or empty.
        if (string.IsNullOrEmpty(_path))
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan throttle = TimeSpan.FromSeconds(_options.CurrentValue.LastSeenThrottleSeconds);

        bool shouldWrite;
        lock (_lock)
        {
            if (_lastSeen.TryGetValue(keyId, out DateTimeOffset prev) && now - prev < throttle)
            {
                // Within the throttle window: coalesce, do nothing.
                return;
            }

            _lastSeen[keyId] = now;
            shouldWrite = true;
        }

        if (shouldWrite)
        {
            PersistBestEffort(now);
        }
    }

    // Persists the current in-memory map to disk atomically (write-temp + rename).
    // Must only be called while NOT holding _lock — it takes the lock to snapshot the map.
    // Any exception is caught and logged; never throws.
    private void PersistBestEffort(DateTimeOffset triggerTime)
    {
        try
        {
            Dictionary<string, DateTimeOffset> snapshot;
            lock (_lock)
            {
                snapshot = new Dictionary<string, DateTimeOffset>(_lastSeen, StringComparer.Ordinal);
            }

            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            JsonObject lastSeenNode = new();
            foreach (KeyValuePair<string, DateTimeOffset> entry in snapshot)
            {
                // ISO-8601 with offset ("O" round-trip format).
                lastSeenNode[entry.Key] = entry.Value.ToString("O");
            }

            JsonObject root = new()
            {
                ["schemaVersion"] = 1,
                ["lastSeen"] = lastSeenNode,
            };

            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            string tempPath = _path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, json);

            // Atomic replace: dmonium may be reading concurrently and must never see half-written JSON.
            File.Move(tempPath, _path!, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist last-seen record (trigger time: {TriggerTime}); " +
                "telemetry skipped, attach continues normally.",
                triggerTime);
        }
    }

    // Best-effort seed of the in-memory map from an existing lastseen.json.
    // Absent or malformed → start empty (log debug/warning, never throw).
    private void LoadExisting()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            string content = File.ReadAllText(_path!);
            JsonNode? root = JsonNode.Parse(content);
            if (root is null)
            {
                _logger.LogWarning("lastseen.json at '{Path}' parsed as null; starting empty.", _path);
                return;
            }

            JsonNode? lastSeenNode = root["lastSeen"];
            if (lastSeenNode is not JsonObject lastSeenObj)
            {
                _logger.LogWarning(
                    "lastseen.json at '{Path}' has no 'lastSeen' object; starting empty.", _path);
                return;
            }

            foreach (KeyValuePair<string, JsonNode?> entry in lastSeenObj)
            {
                string? raw = entry.Value?.GetValue<string>();
                if (DateTimeOffset.TryParse(raw, out DateTimeOffset ts))
                {
                    _lastSeen[entry.Key] = ts;
                }
            }

            _logger.LogDebug(
                "Loaded {Count} last-seen record(s) from '{Path}'.", _lastSeen.Count, _path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not load lastseen.json from '{Path}'; starting with empty map.", _path);
            _lastSeen.Clear();
        }
    }

    /// <summary>Minimal <see cref="IOptionsMonitor{T}"/> for the no-op factory.</summary>
    private sealed class StaticOptions : IOptionsMonitor<NetworkOptions>
    {
        private readonly NetworkOptions _value;

        internal StaticOptions(NetworkOptions value) => _value = value;

        public NetworkOptions CurrentValue => _value;

        public NetworkOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<NetworkOptions, string?> listener) => null;
    }
}
