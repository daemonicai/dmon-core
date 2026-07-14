namespace Dmon.Runtime;

/// <summary>
/// Thrown by <see cref="IRpcClient.RequestAsync{TResult}"/> when the inbound pump exits
/// before a correlated result arrives — because the core process exited (stdout EOF) or
/// the transport read faulted. The channel is gone; the request is not retryable in place.
///
/// This exception is DISTINCT from both:
///   <see cref="OperationCanceledException"/> — the caller cancelled via its token.
///   <see cref="RpcTimeoutException"/> — the core is alive but slow (no result in time).
/// A transport-closed fault means the channel itself is gone ("the core exited").
/// </summary>
public sealed class RpcTransportClosedException : Exception
{
    public string CommandId { get; }

    public RpcTransportClosedException(string commandId, Exception? inner = null)
        : base($"The core exited or the transport closed before a result arrived for command '{commandId}'.", inner)
    {
        CommandId = commandId;
    }
}
