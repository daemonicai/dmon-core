using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Runtime;

/// <summary>
/// Extension methods on <see cref="IRpcTransport"/> for one-shot correlated requests.
///
/// These helpers are intentionally separate from <see cref="IRpcClient"/> because
/// <see cref="IRpcClient"/> starts a continuous read-pump that is lossy and can read
/// past the matched line. The gateway create/load handshake requires
/// <em>read-exactly-until-match-then-stop</em> semantics so that the underlying
/// <see cref="System.IO.TextReader"/> is left positioned right after the matched line —
/// ready for <c>SessionHandler</c> to continue reading without missing any event (design D7).
/// </summary>
public static class RpcTransportExtensions
{
    /// <summary>
    /// Sends <paramref name="command"/> via the transport, then awaits the first
    /// <see cref="ResultEvent"/> whose <see cref="ResultEvent.CommandId"/> equals
    /// <c>command.Id</c>, and returns it.
    ///
    /// No-read-ahead guarantee: the implementation uses <c>await foreach … break</c>.
    /// When a matching event is found the iterator is broken and disposed without calling
    /// <c>MoveNext</c> again, so the underlying reader is left positioned immediately after
    /// the matched line — zero bytes are read ahead.
    ///
    /// Skip-tolerance: non-<see cref="ResultEvent"/> lines and <see cref="ResultEvent"/> lines
    /// with a different <c>CommandId</c> are skipped by continuing the loop. The transport
    /// already skips blank / malformed lines (Group 1).
    ///
    /// Timeout vs. cancellation distinction (design D3):
    /// <list type="bullet">
    ///   <item>When the timeout elapses and the caller's <paramref name="cancellationToken"/>
    ///     is NOT cancelled: faults with <see cref="RpcTimeoutException"/>.</item>
    ///   <item>When the caller's token fires (regardless of timeout state):
    ///     the <see cref="OperationCanceledException"/> propagates unchanged.</item>
    /// </list>
    ///
    /// The base <see cref="ResultEvent"/> is returned so callers can pattern-match concrete
    /// subtypes (including <see cref="CommandErrorEvent"/>) without a cast that could swallow
    /// a command error.
    /// </summary>
    /// <param name="transport">The framing transport to send through and read from.</param>
    /// <param name="command">The command to dispatch. Its <c>Id</c> is used for correlation.</param>
    /// <param name="timeout">
    /// Maximum time to wait for the correlated result after the send completes.
    /// A finite bound is required — use <see cref="Timeout.InfiniteTimeSpan"/> only in tests.
    /// </param>
    /// <param name="cancellationToken">Caller-supplied cancellation. Surfaced as <see cref="OperationCanceledException"/>.</param>
    /// <returns>The first <see cref="ResultEvent"/> correlated to <c>command.Id</c>.</returns>
    /// <exception cref="RpcTimeoutException">
    /// The correlated result did not arrive within <paramref name="timeout"/> and the caller
    /// token was not cancelled.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    /// <exception cref="InvalidOperationException">
    /// The transport's event stream ended (core closed stdout) before the correlated result arrived.
    /// </exception>
    public static async Task<ResultEvent> RequestAsync(
        this IRpcTransport transport,
        Command command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(command);

        await transport.SendAsync(command, cancellationToken).ConfigureAwait(false);

        using CancellationTokenSource timeoutCts = new(timeout);
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            // await foreach + break: when we break, the compiler-generated state machine
            // calls DisposeAsync on the enumerator WITHOUT calling MoveNextAsync again.
            // The underlying CoreProcessRpcTransport.ReadEventsAsync iterator suspends at
            // its current yield point and never reads the next line — zero read-ahead.
            await foreach (Event evt in transport.Events.WithCancellation(linkedCts.Token).ConfigureAwait(false))
            {
                if (evt is ResultEvent result && result.CommandId == command.Id)
                    return result;
                // Other events (different CommandId, non-ResultEvent) are skipped.
            }

            // Stream ended before a matching result arrived (core stdout closed).
            throw new InvalidOperationException(
                $"Core closed stdout before emitting a result for command '{command.Id}'.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timeout fired and the caller did NOT cancel — surface as RpcTimeoutException.
            throw new RpcTimeoutException(command.Id, timeout);
        }
        // If the caller's token fired, the OperationCanceledException propagates unchanged.
    }
}
