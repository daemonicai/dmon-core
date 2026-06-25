namespace Dmon.Network.Sessions;

/// <summary>
/// Represents a live client connection that the session handler can forward event frames to.
/// Decouples <see cref="SessionHandler"/> from the concrete WebSocket implementation (Group 3).
/// </summary>
public interface INetworkConnection
{
    /// <summary>
    /// The device key id that authenticated this connection, or <c>null</c> when auth is
    /// disabled (empty key set). Null connections are never indexed for revocation.
    /// </summary>
    string? KeyId { get; }

    /// <summary>
    /// Sends one text frame (a raw JSONL event line) to the connected client.
    /// </summary>
    ValueTask SendAsync(string frame, CancellationToken cancellationToken);

    /// <summary>
    /// Forcefully aborts the underlying transport without a graceful close handshake.
    /// Called on the evicted connection when a newer attach supersedes it (Group 6).
    /// Aborting makes any blocked <c>ReceiveAsync</c> throw so the forwarding loop exits.
    /// Must not dispose shared resources — disposal is owned by the forwarding loop's
    /// <c>using</c> block.
    /// </summary>
    void Abort();
}
