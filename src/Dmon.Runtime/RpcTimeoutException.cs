namespace Dmon.Runtime;

/// <summary>
/// Thrown by <see cref="IRpcClient.RequestAsync{TResult}"/> when the correlated result
/// event does not arrive within the client's configured timeout.
///
/// This exception is DISTINCT from <see cref="OperationCanceledException"/>:
///   <see cref="RpcTimeoutException"/> — the timeout elapsed (no response from core).
///   <see cref="OperationCanceledException"/> — the caller cancelled via its token.
///
/// The gateway maps this to a <c>core_timeout</c> error code (design D3).
/// </summary>
public sealed class RpcTimeoutException : Exception
{
    public string CommandId { get; }
    public TimeSpan Timeout { get; }

    public RpcTimeoutException(string commandId, TimeSpan timeout)
        : base($"No result received for command '{commandId}' within {timeout.TotalSeconds:F1}s.")
    {
        CommandId = commandId;
        Timeout = timeout;
    }
}
