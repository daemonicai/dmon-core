using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Runtime;

/// <summary>
/// Default <see cref="IRpcClient"/> implementation.
///
/// Lifetime:
///   1. Construct with an <see cref="IRpcTransport"/> and an optional <paramref name="requestTimeout"/>.
///   2. Call <see cref="StartAsync"/> once to start the background pump.
///   3. Use <see cref="SendAsync"/> / <see cref="RequestAsync{TResult}"/> / <see cref="Events"/>.
///   4. Dispose via <see cref="DisposeAsync"/> to stop the pump and drain remaining work.
///
/// The pump enumerates <see cref="IRpcTransport.Events"/> exactly once — the cold
/// IAsyncEnumerable is never re-entered (design D2).
/// </summary>
public sealed class RpcClient : IRpcClient
{
    /// <summary>
    /// Default timeout applied to <see cref="RequestAsync{TResult}"/> when none is supplied
    /// at construction.
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    // Per-broadcast-subscriber channel capacity. Oldest events are dropped for slow consumers.
    private const int BroadcastChannelCapacity = 512;

    private readonly IRpcTransport _transport;
    private readonly TimeSpan _requestTimeout;

    // Pending requests: command id → TCS holding a ResultEvent (typed cast done at resolution).
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ResultEvent>> _pending = new();

    // Active broadcast subscribers. Guarded by _subscribersLock on add/remove; reads are safe
    // as long as we snapshot the list before iterating.
    private readonly List<Channel<Event>> _subscribers = [];
    private readonly object _subscribersLock = new();

    private Task? _pumpTask;
    private CancellationTokenSource? _pumpCts;
    private bool _disposed;

    /// <param name="transport">The underlying framing transport. Must not be started independently.</param>
    /// <param name="requestTimeout">
    /// How long <see cref="RequestAsync{TResult}"/> waits before faulting with
    /// <see cref="RpcTimeoutException"/>. Defaults to <see cref="DefaultRequestTimeout"/>.
    /// </param>
    public RpcClient(IRpcTransport transport, TimeSpan requestTimeout = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _requestTimeout = requestTimeout == default ? DefaultRequestTimeout : requestTimeout;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pumpTask is not null)
            throw new InvalidOperationException("StartAsync has already been called.");

        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pumpTask = RunPumpAsync(_pumpCts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SendAsync(Command command, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        return _transport.SendAsync(command, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<TResult> RequestAsync<TResult>(Command command, CancellationToken cancellationToken)
        where TResult : ResultEvent
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);

        TaskCompletionSource<ResultEvent> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Register BEFORE dispatching — eliminates the race where the result arrives first.
        if (!_pending.TryAdd(command.Id, tcs))
            throw new InvalidOperationException($"A pending request with id '{command.Id}' already exists.");

        try
        {
            await _transport.SendAsync(command, cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource timeoutCts = new(_requestTimeout);
            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            ResultEvent result;
            try
            {
                result = await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Timeout elapsed and the caller did NOT cancel — surface as RpcTimeoutException.
                throw new RpcTimeoutException(command.Id, _requestTimeout);
            }
            // If the caller's token fired, WaitAsync throws OperationCanceledException — let it propagate.

            if (result is not TResult typed)
            {
                throw new InvalidCastException(
                    $"Expected result of type {typeof(TResult).Name} for command '{command.Id}' " +
                    $"but received {result.GetType().Name}.");
            }

            return typed;
        }
        finally
        {
            _pending.TryRemove(command.Id, out _);
        }
    }

    /// <inheritdoc/>
    /// Each access to this property creates a new subscription whose channel is registered
    /// synchronously (before any await), so callers do not lose events between getting the
    /// enumerable and starting iteration.
    public IAsyncEnumerable<Event> Events => CreateSubscription();

    private BroadcastSubscription CreateSubscription()
    {
        Channel<Event> channel = Channel.CreateBounded<Event>(
            new BoundedChannelOptions(BroadcastChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = false,
                AllowSynchronousContinuations = false,
            });

        lock (_subscribersLock)
            _subscribers.Add(channel);

        return new BroadcastSubscription(channel, this);
    }

    internal void RemoveSubscriber(Channel<Event> channel)
    {
        lock (_subscribersLock)
            _subscribers.Remove(channel);
    }

    // ---------------------------------------------------------------
    // IAsyncEnumerable wrapper that holds an already-registered channel.
    // Registration happens at construction (synchronous), so no events
    // are lost between calling client.Events and starting iteration.
    // ---------------------------------------------------------------

    private sealed class BroadcastSubscription : IAsyncEnumerable<Event>
    {
        private readonly Channel<Event> _channel;
        private readonly RpcClient _owner;

        internal BroadcastSubscription(Channel<Event> channel, RpcClient owner)
        {
            _channel = channel;
            _owner = owner;
        }

        public IAsyncEnumerator<Event> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new Enumerator(_channel, _owner, cancellationToken);

        private sealed class Enumerator : IAsyncEnumerator<Event>
        {
            private readonly IAsyncEnumerator<Event> _inner;
            private readonly Channel<Event> _channel;
            private readonly RpcClient _owner;
            private bool _disposed;

            internal Enumerator(Channel<Event> channel, RpcClient owner, CancellationToken cancellationToken)
            {
                _channel = channel;
                _owner = owner;
                _inner = channel.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            }

            public Event Current => _inner.Current;

            public ValueTask<bool> MoveNextAsync() => _inner.MoveNextAsync();

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                    return;
                _disposed = true;
                await _inner.DisposeAsync().ConfigureAwait(false);
                _owner.RemoveSubscriber(_channel);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_pumpCts is not null)
        {
            await _pumpCts.CancelAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
        }

        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }

        // Fault all pending requests so callers don't hang.
        ObjectDisposedException disposed = new(nameof(RpcClient));
        foreach ((string _, TaskCompletionSource<ResultEvent> tcs) in _pending)
            tcs.TrySetException(disposed);
        _pending.Clear();

        // Complete all broadcast subscribers.
        lock (_subscribersLock)
        {
            foreach (Channel<Event> ch in _subscribers)
                ch.Writer.TryComplete();
            _subscribers.Clear();
        }

        // RpcClient is the clear owner of the transport it was constructed with, so it
        // releases the transport's write-serialization semaphore (if any) once the pump
        // has stopped. IRpcTransport itself does not declare IDisposable.
        if (_transport is IDisposable disposableTransport)
            disposableTransport.Dispose();
    }

    // ---------------------------------------------------------------
    // Pump — reads transport.Events exactly once.
    // ---------------------------------------------------------------

    private async Task RunPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (Event evt in _transport.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                // (b) Broadcast to all subscribers first, so no subscriber misses an event
                //     even if it is also correlated to a pending request.
                PublishToBroadcast(evt);

                // (a) Correlation: complete a pending request if this is a matching ResultEvent.
                if (evt is ResultEvent result &&
                    _pending.TryRemove(result.CommandId, out TaskCompletionSource<ResultEvent>? tcs))
                {
                    tcs.TrySetResult(result);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Pump stopped via cancellation — normal shutdown path.
        }
        finally
        {
            // Complete all broadcast subscribers when the pump exits.
            lock (_subscribersLock)
            {
                foreach (Channel<Event> ch in _subscribers)
                    ch.Writer.TryComplete();
            }
        }
    }

    private void PublishToBroadcast(Event evt)
    {
        Channel<Event>[] snapshot;
        lock (_subscribersLock)
            snapshot = [.. _subscribers];

        foreach (Channel<Event> ch in snapshot)
            ch.Writer.TryWrite(evt);
    }
}
