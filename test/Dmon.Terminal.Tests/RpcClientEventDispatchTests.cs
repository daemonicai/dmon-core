using Dmon.Protocol;
using Dmon.Protocol.Events;
using Dmon.Runtime;
using Dmon.Terminal.Tests.Fakes;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Verifies the event-dispatch path introduced by Group 3 of host-rpc-abstraction:
/// <see cref="CoreProcessRpcTransport"/> → <see cref="RpcClient.Events"/> →
/// <see cref="ConsoleEventHandler.HandleRpcEventAsync"/>.
///
/// Uses in-memory streams instead of a real process.
/// </summary>
public sealed class RpcClientEventDispatchTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static string SerializeEvent(Event evt) =>
        System.Text.Json.JsonSerializer.Serialize(evt, WireSerializerOptions.Default);

    private static (RpcClient client, StringWriter commandSink) BuildClientOverStream(string jsonlPayload)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(jsonlPayload);
        MemoryStream ms = new(bytes);
        StreamReader reader = new(ms);
        StringWriter writer = new();
        CoreProcessRpcTransport transport = new(reader, writer);
        return (new RpcClient(transport), writer);
    }

    // ── event delivery ────────────────────────────────────────────────────────

    /// <summary>
    /// Events written as JSONL to the transport stream arrive on <see cref="IRpcClient.Events"/>
    /// and are delivered to <see cref="ConsoleEventHandler.HandleRpcEventAsync"/>.
    /// </summary>
    [Fact]
    public async Task Events_FromTransportStream_AreDeliveredToHandler()
    {
        AgentStartEvent start = new();
        string payload = SerializeEvent(start) + "\n";

        (RpcClient client, _) = BuildClientOverStream(payload);
        await using (client)
        {
            FakeTerminal fake = new();
            List<Dmon.Protocol.Commands.Command> sent = [];
            using CancellationTokenSource cts = new();

            Func<Dmon.Protocol.Commands.Command, CancellationToken, Task> send =
                (cmd, _) => { sent.Add(cmd); return Task.CompletedTask; };

            TerminalRenderer renderer = new(fake);
            InputStateLayer input = new();
            ConsoleEventHandler handler = new(renderer, input, send, cts, () => { }, fake);

            // Subscribe before starting the pump (registration is synchronous).
            IAsyncEnumerable<Event> eventStream = client.Events;
            await client.StartAsync(CancellationToken.None);

            List<Event> received = [];
            await foreach (Event evt in eventStream)
            {
                await handler.HandleRpcEventAsync(evt, CancellationToken.None);
                received.Add(evt);
                // Stream will complete on EOF; break after processing one event.
                break;
            }

            Assert.Single(received);
            Assert.IsType<AgentStartEvent>(received[0]);
        }
    }

    /// <summary>
    /// Subscribe-before-pump ordering: a subscription obtained before <see cref="RpcClient.StartAsync"/>
    /// receives events that arrive immediately after the pump starts — no early-event drop.
    /// </summary>
    [Fact]
    public async Task Events_SubscribeBeforePump_ReceivesEarlyEvents()
    {
        AgentStartEvent start = new();
        AgentStartEvent start2 = new();
        string payload = SerializeEvent(start) + "\n" + SerializeEvent(start2) + "\n";

        (RpcClient client, _) = BuildClientOverStream(payload);
        await using (client)
        {
            // Capture subscription (registers channel synchronously) before StartAsync.
            IAsyncEnumerable<Event> eventStream = client.Events;
            await client.StartAsync(CancellationToken.None);

            List<Event> received = [];
            await foreach (Event evt in eventStream)
                received.Add(evt);

            Assert.Equal(2, received.Count);
            Assert.All(received, e => Assert.IsType<AgentStartEvent>(e));
        }
    }

    /// <summary>
    /// On reload, a fresh <see cref="RpcClient"/> built over the new process stream
    /// reads only the new stream's events, not the previous stream's.
    /// Mirrors the per-session client lifecycle in Program.cs.
    /// </summary>
    [Fact]
    public async Task ReloadLifecycle_FreshClient_ReadsOnlyNewStream()
    {
        AgentReadyEvent ready1 = new() { CoreVersion = "0.0.1", ProtocolVersion = "1.0.0" };
        AgentReadyEvent ready2 = new() { CoreVersion = "0.0.2", ProtocolVersion = "1.0.0" };

        string payload1 = SerializeEvent(ready1) + "\n";
        string payload2 = SerializeEvent(ready2) + "\n";

        // Session 1
        (RpcClient client1, _) = BuildClientOverStream(payload1);
        IAsyncEnumerable<Event> stream1 = client1.Events;
        await client1.StartAsync(CancellationToken.None);

        List<Event> session1Events = [];
        await foreach (Event evt in stream1)
            session1Events.Add(evt);

        await client1.DisposeAsync();

        // Session 2 — simulates the reload path in Program.cs
        (RpcClient client2, _) = BuildClientOverStream(payload2);
        IAsyncEnumerable<Event> stream2 = client2.Events;
        await client2.StartAsync(CancellationToken.None);

        List<Event> session2Events = [];
        await foreach (Event evt in stream2)
            session2Events.Add(evt);

        await client2.DisposeAsync();

        Assert.Single(session1Events);
        Assert.Single(session2Events);

        AgentReadyEvent r1 = Assert.IsType<AgentReadyEvent>(session1Events[0]);
        AgentReadyEvent r2 = Assert.IsType<AgentReadyEvent>(session2Events[0]);
        Assert.Equal("0.0.1", r1.CoreVersion);
        Assert.Equal("0.0.2", r2.CoreVersion);
    }
}
