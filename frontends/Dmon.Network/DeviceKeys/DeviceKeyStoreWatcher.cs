using Dmon.Network.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dmon.Network.DeviceKeys;

/// <summary>
/// Hosted service that watches <c>devices.json</c> for changes and hot-swaps
/// <see cref="DeviceKeySetProvider.Current"/> without restarting the network host.
///
/// Fail-closed semantics (the security crux):
///   1. File present and parseable  → swap Current to the new set (including an
///      explicit empty-devices array, which is the operator's choice to disable auth).
///   2. File malformed / unparseable → keep last-good, log warning.
///   3. File absent or unreadable at runtime → keep last-good, log warning.
///      An atomic write (write-temp-then-rename) briefly removes the file; a delete
///      must NOT fail open to the disabled Empty state. ReadAsync's absent→Empty
///      mapping is correct only at startup; the watcher must NOT call ReadAsync.
///
/// The reload logic is in <see cref="Reload"/> (internally visible for tests) so
/// tests can drive it directly without relying on FileSystemWatcher event timing.
/// </summary>
internal sealed class DeviceKeyStoreWatcher : IHostedService, IDisposable
{
    private readonly DeviceKeySetProvider _provider;
    private readonly DeviceConnectionIndex _index;
    private readonly NetworkDeviceKeyPaths _paths;
    private readonly ILogger<DeviceKeyStoreWatcher> _logger;

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    // Lock guards _debounce timer creation/disposal; the actual swap on _provider
    // is safe without a lock because DeviceKeySetProvider uses a volatile field.
    private readonly object _debounceLock = new();

    // Quiet window before a burst of FSW events triggers one Reload().
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);

    public DeviceKeyStoreWatcher(
        DeviceKeySetProvider provider,
        DeviceConnectionIndex index,
        NetworkDeviceKeyPaths paths,
        ILogger<DeviceKeyStoreWatcher> logger)
    {
        _provider = provider;
        _index = index;
        _paths = paths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_paths.DevicesPath)
            ?? throw new InvalidOperationException(
                $"Cannot determine directory for device key store path '{_paths.DevicesPath}'.");

        // Create the store directory if absent — benign; the network host owns this directory.
        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory)
        {
            Filter = Path.GetFileName(_paths.DevicesPath),
            NotifyFilter = NotifyFilters.LastWrite
                         | NotifyFilters.FileName
                         | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Deleted += OnFileEvent;

        _logger.LogInformation(
            "Device-key store watcher started, watching '{Directory}' for '{Filter}'.",
            directory, _watcher.Filter);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeWatcherAndTimer();
        return Task.CompletedTask;
    }

    public void Dispose() => DisposeWatcherAndTimer();

    private void DisposeWatcherAndTimer()
    {
        _watcher?.Dispose();
        _watcher = null;

        lock (_debounceLock)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
        // An in-flight Reload() is intentionally allowed to complete: it touches only
        // immutable paths, the static reader, and the volatile provider swap — none of
        // which disposal tears down. A late callback after disposal is a harmless no-op.
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Reset the debounce timer on every event so a burst produces one Reload().
        lock (_debounceLock)
        {
            if (_debounce is null)
            {
                _debounce = new Timer(_ => Reload(), state: null, DebounceDelay, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Reads the file, parses it, and swaps <see cref="DeviceKeySetProvider.Current"/>
    /// if successful. On any failure the last-good set is retained and a warning is logged.
    ///
    /// After a successful swap, any keyId that left the active set (revoked, deleted, or
    /// blank-secret-filtered) has its live connections fenced via <see cref="INetworkConnection.Abort"/>.
    /// The swap happens first so reconnect attempts during fencing already see the new set.
    ///
    /// Internally visible so tests can drive it directly without FSW event timing.
    ///</summary>
    internal void Reload()
    {
        string path = _paths.DevicesPath;
        string content;
        try
        {
            // Read the raw bytes ourselves — do NOT call DeviceKeyStoreReader.ReadAsync,
            // which maps absent→Empty. At runtime, absent means a transient mid-replace
            // state, not an operator intent to disable; keep last-good in that case.
            content = File.ReadAllText(path);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation — do not treat it as a reload failure.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Device-key store reload skipped: could not read '{Path}'. " +
                "Retaining last-good key set.",
                path);
            return;
        }

        DeviceKeySet newSet;
        try
        {
            newSet = DeviceKeyStoreReader.Parse(content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Device-key store reload skipped: could not parse '{Path}'. " +
                "Retaining last-good key set.",
                path);
            return;
        }

        // Capture the previous active set before the swap so we can diff it.
        // The diff is "left the active set" — covers revoked (revokedAt set), row-deleted,
        // and blank-secret-filtered entries — all mean the credential can no longer authenticate.
        DeviceKeySet previous = _provider.Current;

        // Swap first: any reconnect attempt during subsequent fencing already sees the new set.
        _provider.Update(newSet);

        if (newSet.IsEmpty)
        {
            _logger.LogWarning(
                "Device-key store reloaded from '{Path}': active set is now EMPTY " +
                "(auth disabled — operator choice).",
                path);
        }
        else
        {
            _logger.LogInformation(
                "Device-key store reloaded from '{Path}': {Count} active credential(s).",
                path, newSet.Entries.Count);
        }

        // Fence live connections whose keyId left the active set.
        HashSet<string> newKeyIds = [.. newSet.Entries.Select(e => e.KeyId)];
        foreach (DeviceCredential entry in previous.Entries)
        {
            if (newKeyIds.Contains(entry.KeyId))
            {
                continue;
            }

            IReadOnlyCollection<INetworkConnection> connections = _index.GetConnections(entry.KeyId);
            if (connections.Count == 0)
            {
                continue;
            }

            _logger.LogWarning(
                "Device key '{KeyId}' left the active set; fencing {Count} live connection(s).",
                entry.KeyId, connections.Count);

            foreach (INetworkConnection conn in connections)
                conn.Abort();
        }
    }
}
