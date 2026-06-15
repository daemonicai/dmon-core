using System.Collections.Concurrent;

namespace Dmon.Gateway.Sessions;

/// <summary>
/// Cross-session singleton index mapping a device key id to its currently-live connections.
/// Used by Group 6 revocation: when a key is revoked the watcher calls
/// <see cref="GetConnections"/> and <see cref="IGatewayConnection.Abort"/>s each entry.
///
/// Thread-safety: all mutations are atomic via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// <see cref="GetConnections"/> returns a snapshot so callers can iterate without holding a lock.
/// </summary>
internal sealed class DeviceConnectionIndex
{
    // Outer key: keyId. Inner value: set of live connections for that key (value byte unused).
    // Reference equality is used for connection identity, which matches object identity for
    // the WebSocketGatewayConnection instances used in production.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<IGatewayConnection, byte>> _index = new();

    /// <summary>
    /// Records <paramref name="connection"/> as live under <paramref name="keyId"/>.
    /// </summary>
    public void Add(string keyId, IGatewayConnection connection)
    {
        ConcurrentDictionary<IGatewayConnection, byte> bucket =
            _index.GetOrAdd(keyId, _ => new ConcurrentDictionary<IGatewayConnection, byte>(ReferenceEqualityComparer.Instance));
        bucket.TryAdd(connection, 0);
    }

    /// <summary>
    /// Removes <paramref name="connection"/> from the <paramref name="keyId"/> bucket.
    /// Idempotent: removing an absent connection is a no-op. Cleans up an empty bucket.
    /// </summary>
    public void Remove(string keyId, IGatewayConnection connection)
    {
        if (!_index.TryGetValue(keyId, out ConcurrentDictionary<IGatewayConnection, byte>? bucket))
            return;

        bucket.TryRemove(connection, out _);

        // Clean up the bucket if it is now empty. Racing additions are tolerated: if Add
        // re-creates the bucket between our TryRemove and the TryRemove below, the bucket
        // stays and the entry is not incorrectly deleted.
        if (bucket.IsEmpty)
            _index.TryRemove(new KeyValuePair<string, ConcurrentDictionary<IGatewayConnection, byte>>(keyId, bucket));
    }

    /// <summary>
    /// Returns a point-in-time snapshot of all live connections for <paramref name="keyId"/>.
    /// Returns an empty collection for an unknown key id.
    /// The snapshot is safe to iterate while the index is mutated concurrently.
    /// </summary>
    public IReadOnlyCollection<IGatewayConnection> GetConnections(string keyId)
    {
        if (!_index.TryGetValue(keyId, out ConcurrentDictionary<IGatewayConnection, byte>? bucket))
            return [];

        return [.. bucket.Keys];
    }
}
