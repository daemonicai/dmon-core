namespace Dmon.Gateway.Sessions;

/// <summary>
/// Represents a live client connection that the session handler can forward event frames to.
/// Decouples <see cref="SessionHandler"/> from the concrete WebSocket implementation (Group 3).
/// </summary>
public interface IGatewayConnection
{
    /// <summary>
    /// Sends one text frame (a raw JSONL event line) to the connected client.
    /// </summary>
    ValueTask SendAsync(string frame, CancellationToken cancellationToken);
}
