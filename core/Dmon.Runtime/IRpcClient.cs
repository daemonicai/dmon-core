using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Runtime;

/// <summary>
/// Request/response layer over <see cref="IRpcTransport"/>.
///
/// A single background read loop drains <see cref="IRpcTransport.Events"/> exactly once
/// (the cold enumerable is never re-enumerated). Every inbound event is routed to both:
///   (a) the pending-request correlation registry — if the event is a <see cref="ResultEvent"/>
///       whose <see cref="ResultEvent.CommandId"/> matches a registered command id, the
///       corresponding <see cref="System.Threading.Tasks.TaskCompletionSource{T}"/> is completed; and
///   (b) the broadcast channel — all events (including unmatched ones) are written so that
///       callers consuming <see cref="Events"/> observe every event from the core.
///
/// Call <see cref="StartAsync"/> once before issuing any commands.
/// </summary>
public interface IRpcClient : IAsyncDisposable
{
    /// <summary>
    /// Starts the background event-pump loop. Must be called exactly once before any
    /// <see cref="SendAsync"/> or <see cref="RequestAsync{TResult}"/> call.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a command without waiting for a correlated result.
    /// </summary>
    Task SendAsync(Command command, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a command and waits for the correlated <typeparamref name="TResult"/> event.
    ///
    /// Correlation is by <see cref="ResultEvent.CommandId"/> == <paramref name="command"/>.Id.
    /// Registration happens BEFORE the command is dispatched, eliminating the race where the
    /// result arrives before registration.
    ///
    /// Faults with:
    ///   <see cref="RpcTimeoutException"/>       — result did not arrive within the client's
    ///                                             configured timeout (distinct from cancellation).
    ///   <see cref="OperationCanceledException"/> — <paramref name="cancellationToken"/> was cancelled.
    ///   <see cref="InvalidCastException"/>       — a correlated <see cref="ResultEvent"/> arrived but
    ///                                             is not assignable to <typeparamref name="TResult"/>;
    ///                                             this includes <see cref="CommandErrorEvent"/> when
    ///                                             <typeparamref name="TResult"/> is a narrower type.
    /// </summary>
    Task<TResult> RequestAsync<TResult>(Command command, CancellationToken cancellationToken)
        where TResult : ResultEvent;

    /// <summary>
    /// Broadcast stream of all events received from the core, including events not
    /// correlated to any pending request. Each call to <c>await foreach</c> creates an
    /// independent consumer backed by its own bounded channel; events are buffered up to
    /// the channel capacity and the oldest are dropped if a slow consumer falls behind.
    /// The stream completes when the pump loop exits (end-of-stream or pump cancelled).
    /// </summary>
    IAsyncEnumerable<Event> Events { get; }
}
