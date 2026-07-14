using System.Text.Json;
using System.Threading.Channels;
using Dmon.Protocol;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Protocol.Sessions;
using Dmon.Runtime;

namespace Dmon.Runtime.Tests;

/// <summary>
/// Covers tasks 2.1–2.4: IRpcClient request/response correlation, broadcast fan-out,
/// timeout vs. cancellation distinction, and single-reader pump invariant.
/// All tests use an in-memory transport double — no process is spawned.
/// </summary>
public sealed class RpcClientTests
{
    // ---------------------------------------------------------------
    // Test doubles
    // ---------------------------------------------------------------

    /// <summary>
    /// In-memory <see cref="IRpcTransport"/> whose event stream is driven by
    /// <see cref="FeedAsync"/>. Allows tests to inject events after the client starts.
    /// </summary>
    private sealed class FeedableTransport : IRpcTransport
    {
        private readonly Channel<Event> _channel = Channel.CreateUnbounded<Event>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });

        public List<Command> SentCommands { get; } = [];

        /// <summary>Push an event into the stream.</summary>
        public void Feed(Event evt) => _channel.Writer.TryWrite(evt);

        /// <summary>Signal end-of-stream (core process exited).</summary>
        public void Complete() => _channel.Writer.Complete();

        /// <summary>
        /// Fault the stream — completing the writer WITH an exception makes the pump's
        /// <c>ReadAllAsync</c> throw it when drained (a non-cancellation pump fault).
        /// </summary>
        public void Fault(Exception ex) => _channel.Writer.Complete(ex);

        public Task SendAsync(Command command, CancellationToken cancellationToken)
        {
            SentCommands.Add(command);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<Event> Events => _channel.Reader.ReadAllAsync();
    }

    /// <summary>
    /// Builds a <see cref="SessionCreatedResultEvent"/> with the given <paramref name="commandId"/>.
    /// </summary>
    private static SessionCreatedResultEvent MakeSessionCreated(string commandId) =>
        new()
        {
            CommandId = commandId,
            Session = new SessionMeta
            {
                Id = "sess-1",
                Created = DateTimeOffset.UtcNow,
                Modified = DateTimeOffset.UtcNow,
            },
        };

    /// <summary>
    /// Serializes an event as the core would emit it, then parses it back through the
    /// wire transport path (base-type deserialization). Used to verify the full round-trip
    /// but the feedable transport accepts typed objects directly.
    /// </summary>
    private static string SerializeEvent(Event evt) =>
        JsonSerializer.Serialize(evt, WireSerializerOptions.Default);

    // ---------------------------------------------------------------
    // Helper: start an RpcClient over a FeedableTransport.
    // ---------------------------------------------------------------

    private static async Task<(RpcClient client, FeedableTransport transport)> StartClientAsync(
        TimeSpan? timeout = null)
    {
        FeedableTransport transport = new();
        RpcClient client = timeout.HasValue
            ? new RpcClient(transport, timeout.Value)
            : new RpcClient(transport);
        await client.StartAsync(CancellationToken.None);
        return (client, transport);
    }

    // ---------------------------------------------------------------
    // 2.3 / 2.4 — Request completes on correlated typed result
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestAsync_CompletesOnCorrelatedResult()
    {
        (RpcClient client, FeedableTransport transport) = await StartClientAsync();
        await using (client)
        {
            SessionCreateCommand cmd = new() { Id = "cmd-session-1" };

            Task<SessionCreatedResultEvent> request =
                client.RequestAsync<SessionCreatedResultEvent>(cmd, CancellationToken.None);

            // Feed the correlated result.
            transport.Feed(MakeSessionCreated("cmd-session-1"));

            SessionCreatedResultEvent result = await request.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("cmd-session-1", result.CommandId);
            Assert.Equal("sess-1", result.Session.Id);
            // Command was sent to the transport.
            Assert.Single(transport.SentCommands);
        }
    }

    // ---------------------------------------------------------------
    // 2.2 / 2.4 — Unrelated result leaves pending request pending
    //             AND reaches broadcast consumer
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestAsync_UnrelatedResult_DoesNotCompleteRequest_AndBroadcastSeesIt()
    {
        (RpcClient client, FeedableTransport transport) = await StartClientAsync(
            timeout: TimeSpan.FromSeconds(10));
        await using (client)
        {
            SessionCreateCommand cmd = new() { Id = "cmd-target" };

            // Subscribe to broadcast BEFORE starting the request.
            // Capture the IAsyncEnumerable on THIS thread so the channel is registered
            // synchronously before any events are fed.
            List<Event> broadcastReceived = [];
            using CancellationTokenSource broadcastCts = new();
            IAsyncEnumerable<Event> broadcastStream = client.Events;
            Task broadcastConsumer = Task.Run(async () =>
            {
                await foreach (Event evt in broadcastStream.WithCancellation(broadcastCts.Token))
                    broadcastReceived.Add(evt);
            });

            Task<SessionCreatedResultEvent> request =
                client.RequestAsync<SessionCreatedResultEvent>(cmd, CancellationToken.None);

            // Feed an event for a DIFFERENT command id — must not complete "cmd-target".
            SessionCreatedResultEvent unrelated = MakeSessionCreated("cmd-other");
            transport.Feed(unrelated);

            // Give the pump a moment to process.
            await Task.Delay(100);

            // (a) The request for "cmd-target" must still be pending (not completed).
            Assert.False(request.IsCompleted, "Request for 'cmd-target' must remain pending.");

            // (b) Broadcast consumer must have received the unrelated event.
            Assert.Single(broadcastReceived);
            SessionCreatedResultEvent? received = Assert.IsType<SessionCreatedResultEvent>(broadcastReceived[0]);
            Assert.Equal("cmd-other", received.CommandId);

            // Now complete the pending request so we can clean up.
            transport.Feed(MakeSessionCreated("cmd-target"));
            await request.WaitAsync(TimeSpan.FromSeconds(5));

            await broadcastCts.CancelAsync();
            try { await broadcastConsumer; } catch (OperationCanceledException) { }
        }
    }

    // ---------------------------------------------------------------
    // 2.3 / 2.4 — Timeout faults with RpcTimeoutException (not cancellation)
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestAsync_Timeout_ThrowsRpcTimeoutException_NotOperationCanceled()
    {
        // Very short timeout so the test completes quickly.
        (RpcClient client, FeedableTransport transport) = await StartClientAsync(
            timeout: TimeSpan.FromMilliseconds(100));
        await using (client)
        {
            SessionCreateCommand cmd = new() { Id = "cmd-timeout" };

            // No event fed — the timeout will fire first.
            Exception? thrown = null;
            try
            {
                await client.RequestAsync<SessionCreatedResultEvent>(cmd, CancellationToken.None);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            // Must be RpcTimeoutException, NOT OperationCanceledException.
            RpcTimeoutException rpcEx = Assert.IsType<RpcTimeoutException>(thrown);
            Assert.Equal("cmd-timeout", rpcEx.CommandId);
            Assert.False(thrown is OperationCanceledException,
                "Timeout must not surface as OperationCanceledException.");

            transport.Complete();
        }
    }

    // ---------------------------------------------------------------
    // 2.3 / 2.4 — Caller cancellation faults with OperationCanceledException (not timeout)
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestAsync_CallerCancels_ThrowsOperationCanceledException_NotTimeout()
    {
        // Long timeout to ensure it does NOT fire before the caller cancels.
        (RpcClient client, FeedableTransport transport) = await StartClientAsync(
            timeout: TimeSpan.FromSeconds(30));
        await using (client)
        {
            SessionCreateCommand cmd = new() { Id = "cmd-cancel" };

            using CancellationTokenSource cts = new();

            Task<SessionCreatedResultEvent> request =
                client.RequestAsync<SessionCreatedResultEvent>(cmd, cts.Token);

            // Cancel before any event arrives.
            await cts.CancelAsync();

            Exception? thrown = null;
            try { await request; }
            catch (Exception ex) { thrown = ex; }

            // Must be OperationCanceledException (or TaskCanceledException), NOT RpcTimeoutException.
            Assert.IsAssignableFrom<OperationCanceledException>(thrown);
            Assert.False(thrown is RpcTimeoutException,
                "Caller cancellation must not surface as RpcTimeoutException.");

            transport.Complete();
        }
    }

    // ---------------------------------------------------------------
    // 2.3 — Type mismatch faults with InvalidCastException (not hang)
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestAsync_TypeMismatch_ThrowsInvalidCastException()
    {
        (RpcClient client, FeedableTransport transport) = await StartClientAsync(
            timeout: TimeSpan.FromSeconds(10));
        await using (client)
        {
            SessionCreateCommand cmd = new() { Id = "cmd-mismatch" };

            // Request expects SessionCreatedResultEvent, but core returns a SessionLoadedResultEvent.
            Task<SessionLoadedResultEvent> request =
                client.RequestAsync<SessionLoadedResultEvent>(cmd, CancellationToken.None);

            // Feed a SessionCreatedResultEvent (wrong type) with matching command id.
            transport.Feed(MakeSessionCreated("cmd-mismatch"));

            Exception? thrown = null;
            try { await request.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { thrown = ex; }

            Assert.IsType<InvalidCastException>(thrown);

            transport.Complete();
        }
    }

    // ---------------------------------------------------------------
    // 2.2 — Broadcast delivers all events (even non-ResultEvents)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Events_BroadcastDeliversAllEvents_IncludingNonResult()
    {
        (RpcClient client, FeedableTransport transport) = await StartClientAsync();
        await using (client)
        {
            List<Event> received = [];
            using CancellationTokenSource cts = new();
            // Capture on this thread so channel registration is synchronous.
            IAsyncEnumerable<Event> stream = client.Events;
            Task consumer = Task.Run(async () =>
            {
                await foreach (Event evt in stream.WithCancellation(cts.Token))
                    received.Add(evt);
            });

            AgentReadyEvent ready = new() { ProtocolVersion = "1.0.0", CoreVersion = "0.1.0" };
            AgentStartEvent start = new();
            transport.Feed(ready);
            transport.Feed(start);

            await Task.Delay(100);

            Assert.Equal(2, received.Count);
            Assert.IsType<AgentReadyEvent>(received[0]);
            Assert.IsType<AgentStartEvent>(received[1]);

            await cts.CancelAsync();
            try { await consumer; } catch (OperationCanceledException) { }

            transport.Complete();
        }
    }

    // ---------------------------------------------------------------
    // 2.2 — Multiple independent broadcast subscribers each see all events
    // ---------------------------------------------------------------

    [Fact]
    public async Task Events_MultipleSubscribers_EachReceiveAllEvents()
    {
        (RpcClient client, FeedableTransport transport) = await StartClientAsync();
        await using (client)
        {
            List<Event> receivedA = [];
            List<Event> receivedB = [];
            using CancellationTokenSource cts = new();

            // Capture both subscriptions synchronously on this thread before any events are fed.
            IAsyncEnumerable<Event> streamA = client.Events;
            IAsyncEnumerable<Event> streamB = client.Events;

            Task consumerA = Task.Run(async () =>
            {
                await foreach (Event evt in streamA.WithCancellation(cts.Token))
                    receivedA.Add(evt);
            });
            Task consumerB = Task.Run(async () =>
            {
                await foreach (Event evt in streamB.WithCancellation(cts.Token))
                    receivedB.Add(evt);
            });

            // Give both consumers time to subscribe before feeding.
            await Task.Delay(50);

            AgentReadyEvent ready = new() { ProtocolVersion = "1.0.0", CoreVersion = "0.2.0" };
            transport.Feed(ready);

            await Task.Delay(100);

            Assert.Single(receivedA);
            Assert.Single(receivedB);

            await cts.CancelAsync();
            try { await consumerA; } catch (OperationCanceledException) { }
            try { await consumerB; } catch (OperationCanceledException) { }

            transport.Complete();
        }
    }

    // ---------------------------------------------------------------
    // 2.2 — Pump enumerates transport.Events exactly once
    //         Verified structurally: FeedableTransport uses a single Channel
    //         whose ReadAllAsync only completes once; if two enumerations were
    //         started they would race and drop events. This test asserts that
    //         all fed events arrive at the broadcast subscriber — no drops.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Pump_EnumeratesTransportEventsExactlyOnce_NoDrop()
    {
        (RpcClient client, FeedableTransport transport) = await StartClientAsync();
        await using (client)
        {
            const int eventCount = 20;
            List<Event> received = [];
            using CancellationTokenSource cts = new();
            // Capture on this thread to register the channel before events are fed.
            IAsyncEnumerable<Event> stream = client.Events;
            Task consumer = Task.Run(async () =>
            {
                await foreach (Event evt in stream.WithCancellation(cts.Token))
                    received.Add(evt);
            });

            // Feed 20 events rapidly.
            for (int i = 0; i < eventCount; i++)
                transport.Feed(new AgentStartEvent());

            await Task.Delay(200);

            Assert.Equal(eventCount, received.Count);

            await cts.CancelAsync();
            try { await consumer; } catch (OperationCanceledException) { }
            transport.Complete();
        }
    }

    // ---------------------------------------------------------------
    // 2.1 — SendAsync delegates to transport (fire-and-forget path)
    // ---------------------------------------------------------------

    [Fact]
    public async Task SendAsync_DelegatesToTransport()
    {
        (RpcClient client, FeedableTransport transport) = await StartClientAsync();
        await using (client)
        {
            SessionListCommand cmd = new() { Id = "cmd-list-1" };
            await client.SendAsync(cmd, CancellationToken.None);

            Assert.Single(transport.SentCommands);
            Assert.IsType<SessionListCommand>(transport.SentCommands[0]);

            transport.Complete();
        }
    }

    // ---------------------------------------------------------------
    // Dispose — pending requests are faulted, broadcast subscribers complete
    // ---------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_FaultsPendingRequests()
    {
        (RpcClient client, FeedableTransport transport) = await StartClientAsync(
            timeout: TimeSpan.FromSeconds(30));

        SessionCreateCommand cmd = new() { Id = "cmd-dispose" };
        Task<SessionCreatedResultEvent> request =
            client.RequestAsync<SessionCreatedResultEvent>(cmd, CancellationToken.None);

        // Dispose before any result arrives.
        await client.DisposeAsync();

        Exception? thrown = null;
        try { await request; }
        catch (Exception ex) { thrown = ex; }

        Assert.NotNull(thrown);
        // Faulted with ObjectDisposedException (or wrapping it).
        Assert.True(thrown is ObjectDisposedException || thrown?.InnerException is ObjectDisposedException,
            $"Expected ObjectDisposedException, got {thrown?.GetType().Name}");

        transport.Complete();
    }

    // ---------------------------------------------------------------
    // 4.1 — Core exit (stdout EOF) faults pending with RpcTransportClosedException
    //        promptly — NOT RpcTimeoutException, NOT a 30s hang.
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestAsync_CoreExitsEof_FaultsWithTransportClosed_NotTimeout()
    {
        // Long timeout: if the fix regressed, this would hang to the timeout instead of
        // faulting promptly. The short assertion timeout below turns that into a fast failure.
        (RpcClient client, FeedableTransport transport) = await StartClientAsync(
            timeout: TimeSpan.FromSeconds(30));
        await using (client)
        {
            SessionCreateCommand cmd = new() { Id = "cmd-eof" };

            Task<SessionCreatedResultEvent> request =
                client.RequestAsync<SessionCreatedResultEvent>(cmd, CancellationToken.None);

            // Core exits: no correlated result, just EOF.
            transport.Complete();

            RpcTransportClosedException ex = await Assert.ThrowsAsync<RpcTransportClosedException>(
                () => request.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal("cmd-eof", ex.CommandId);
            Assert.Null(ex.InnerException);
            // ThrowsAsync<RpcTransportClosedException> already proves the fault type is
            // distinct from RpcTimeoutException (they are unrelated sealed types).
        }
    }

    // ---------------------------------------------------------------
    // 4.1 — A non-cancellation pump fault faults pending with
    //        RpcTransportClosedException carrying the cause, and is observed
    //        (does not resurface at DisposeAsync).
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestAsync_PumpFaults_FaultsWithTransportClosedCarryingCause_AndDisposeIsClean()
    {
        (RpcClient client, FeedableTransport transport) = await StartClientAsync(
            timeout: TimeSpan.FromSeconds(30));
        await using (client)
        {
            SessionCreateCommand cmd = new() { Id = "cmd-fault" };

            Task<SessionCreatedResultEvent> request =
                client.RequestAsync<SessionCreatedResultEvent>(cmd, CancellationToken.None);

            IOException cause = new("transport read failed");
            transport.Fault(cause);

            RpcTransportClosedException ex = await Assert.ThrowsAsync<RpcTransportClosedException>(
                () => request.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal("cmd-fault", ex.CommandId);
            Assert.Same(cause, ex.InnerException);

            // The non-OCE fault must have been observed inside the pump: disposal (via the
            // enclosing await using) completes cleanly and does not rethrow the injected exception.
        }
    }

    // ---------------------------------------------------------------
    // 4.1 — A STRAY OperationCanceledException (disposal token NOT cancelled,
    //        e.g. a dying transport read) routes to the transport-closed fault
    //        rather than being misclassified as disposal-cancellation (which
    //        would leave the request to hang to the request timeout).
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestAsync_StrayOperationCanceled_FaultsWithTransportClosed_NotHang()
    {
        (RpcClient client, FeedableTransport transport) = await StartClientAsync(
            timeout: TimeSpan.FromSeconds(30));
        await using (client)
        {
            SessionCreateCommand cmd = new() { Id = "cmd-stray-oce" };

            Task<SessionCreatedResultEvent> request =
                client.RequestAsync<SessionCreatedResultEvent>(cmd, CancellationToken.None);

            // The pump read surfaces an OCE while disposal was NEVER requested.
            TaskCanceledException cause = new("transport read cancelled");
            transport.Fault(cause);

            RpcTransportClosedException ex = await Assert.ThrowsAsync<RpcTransportClosedException>(
                () => request.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal("cmd-stray-oce", ex.CommandId);
            Assert.Same(cause, ex.InnerException);
        }
    }
}
