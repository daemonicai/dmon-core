using System.IO;
using System.Text;
using System.Text.Json;
using Dmon.Protocol;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Runtime;

namespace Dmon.Runtime.Tests;

/// <summary>
/// Covers tasks 1.1–1.4: IRpcTransport framing, blank-line skip,
/// malformed-line tolerance, and clean end-of-stream completion.
/// All tests use in-memory streams — no process is spawned.
/// </summary>
public sealed class RpcTransportTests
{
    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// A <see cref="TextWriter"/> that captures every byte written into a
    /// <see cref="StringBuilder"/> for exact-bytes assertions.
    /// </summary>
    private sealed class CapturingWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => _sb.Append(value);

        public override void Write(string? value) => _sb.Append(value);

        public override void Write(ReadOnlySpan<char> buffer) => _sb.Append(buffer);

        public string Captured => _sb.ToString();
    }

    /// <summary>
    /// A <see cref="TextReader"/> whose content is set at construction time,
    /// simulating a completed output stream.
    /// </summary>
    private static TextReader MakeReader(params string[] lines)
        => new StringReader(string.Join("\n", lines));

    /// <summary>
    /// A thread-safe capturing <see cref="TextWriter"/> for the concurrency regression test.
    /// Unlike <see cref="CapturingWriter"/> (a plain, non-thread-safe <see cref="StringBuilder"/>
    /// wrapper), every append here is guarded by a lock. The async write path additionally
    /// yields before appending, forcing a real scheduling point so that pre-fix code — which
    /// issues the JSON body and the trailing LF as two separate writes — actually interleaves
    /// across concurrent callers instead of happening to run back-to-back on one thread.
    /// </summary>
    private sealed class ThreadSafeCapturingWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();
        private readonly object _lock = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override async Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken)
        {
            await Task.Yield();
            lock (_lock)
                _sb.Append(buffer.Span);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
        }

        public string Captured
        {
            get { lock (_lock) return _sb.ToString(); }
        }
    }

    /// <summary>
    /// Serializes an event as the core would emit it — through the base <see cref="Event"/>
    /// type with <see cref="WireSerializerOptions.Default"/>.
    /// </summary>
    private static string SerializeEvent(Event evt)
        => JsonSerializer.Serialize(evt, WireSerializerOptions.Default);

    // ---------------------------------------------------------------
    // 1.2 / 1.1 — Send path: exact-bytes framing
    // ---------------------------------------------------------------

    [Fact]
    public async Task SendAsync_WritesJsonPlusLineFeed_ExactBytes()
    {
        CapturingWriter writer = new();
        TextReader reader = MakeReader();
        CoreProcessRpcTransport transport = new(reader, writer);

        SessionCreateCommand command = new() { Id = "cmd-1" };
        await transport.SendAsync(command, CancellationToken.None);

        string captured = writer.Captured;

        // Must end with exactly one LF, not CRLF.
        Assert.EndsWith("\n", captured);
        Assert.DoesNotContain("\r", captured);

        // The line (sans trailing LF) must be valid JSON with the "type" discriminator.
        string json = captured.TrimEnd('\n');
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("type", out JsonElement typeEl),
            "Serialized command must contain a \"type\" discriminator.");
        Assert.Equal("session.create", typeEl.GetString());

        // Must also carry the "id" field.
        Assert.True(doc.RootElement.TryGetProperty("id", out JsonElement idEl));
        Assert.Equal("cmd-1", idEl.GetString());
    }

    [Fact]
    public async Task SendAsync_ExactlyOneLf_NoCrLf()
    {
        CapturingWriter writer = new();
        CoreProcessRpcTransport transport = new(MakeReader(), writer);

        TurnSubmitCommand command = new() { Id = "cmd-2", Message = "hello" };
        await transport.SendAsync(command, CancellationToken.None);

        string captured = writer.Captured;
        int lfCount = captured.Count(c => c == '\n');
        int crCount = captured.Count(c => c == '\r');

        Assert.Equal(1, lfCount);
        Assert.Equal(0, crCount);
    }

    [Fact]
    public async Task SendAsync_TypeDiscriminator_ReflectsConcreteCommandType()
    {
        CapturingWriter writer = new();
        CoreProcessRpcTransport transport = new(MakeReader(), writer);

        TurnSubmitCommand command = new() { Id = "cmd-3", Message = "test" };
        await transport.SendAsync(command, CancellationToken.None);

        string json = writer.Captured.TrimEnd('\n');
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("turn.submit", doc.RootElement.GetProperty("type").GetString());
    }

    // ---------------------------------------------------------------
    // Write-serialization regression (runtime-rpc-write-serialization 2.2)
    // ---------------------------------------------------------------

    [Fact]
    public async Task SendAsync_ConcurrentCallers_FramesNotInterleaved()
    {
        const int callerCount = 50;
        ThreadSafeCapturingWriter writer = new();
        CoreProcessRpcTransport transport = new(MakeReader(), writer);

        List<TurnSubmitCommand> commands = Enumerable.Range(0, callerCount)
            .Select(i => new TurnSubmitCommand { Id = $"cmd-{i}", Message = $"message-{i}" })
            .ToList();

        await Task.WhenAll(commands.Select(c => transport.SendAsync(c, CancellationToken.None)));

        string captured = writer.Captured;
        Assert.DoesNotContain("\r", captured);

        string[] lines = captured.Split('\n');
        // Trailing split entry after the final LF is empty — every real frame ends in "\n".
        Assert.Equal(string.Empty, lines[^1]);
        string[] frames = lines[..^1];

        Assert.Equal(callerCount, frames.Length);

        List<string> receivedIds = [];
        foreach (string frame in frames)
        {
            Command? deserialized = JsonSerializer.Deserialize<Command>(frame, WireSerializerOptions.Default);
            Assert.NotNull(deserialized);
            TurnSubmitCommand typed = Assert.IsType<TurnSubmitCommand>(deserialized);
            receivedIds.Add(typed.Id);
        }

        List<string> expectedIds = [.. commands.Select(c => c.Id).Order()];
        List<string> actualIds = [.. receivedIds.Order()];
        Assert.Equal(expectedIds, actualIds);
    }

    // ---------------------------------------------------------------
    // 1.2 — Receive path: events are deserialized and yielded
    // ---------------------------------------------------------------

    [Fact]
    public async Task Events_YieldsDeserializedEvents()
    {
        AgentReadyEvent evt = new() { ProtocolVersion = "1.0.0", CoreVersion = "0.1.0" };
        TextReader reader = MakeReader(SerializeEvent(evt));
        CoreProcessRpcTransport transport = new(reader, new CapturingWriter());

        List<Event> received = [];
        await foreach (Event e in transport.Events)
            received.Add(e);

        Event single = Assert.Single(received);
        AgentReadyEvent ready = Assert.IsType<AgentReadyEvent>(single);
        Assert.Equal("1.0.0", ready.ProtocolVersion);
        Assert.Equal("0.1.0", ready.CoreVersion);
    }

    // ---------------------------------------------------------------
    // 1.3 — Resilience: blank-line skip
    // ---------------------------------------------------------------

    [Fact]
    public async Task Events_SkipsBlankLines()
    {
        AgentReadyEvent evt = new() { ProtocolVersion = "1.0.0", CoreVersion = "0.2.0" };
        // Interleave blank and whitespace-only lines.
        TextReader reader = MakeReader(
            "",
            "   ",
            "\t",
            SerializeEvent(evt),
            "");
        CoreProcessRpcTransport transport = new(reader, new CapturingWriter());

        List<Event> received = [];
        await foreach (Event e in transport.Events)
            received.Add(e);

        Assert.Single(received);
    }

    // ---------------------------------------------------------------
    // 1.3 — Resilience: malformed-line tolerance
    // ---------------------------------------------------------------

    [Fact]
    public async Task Events_MalformedLine_EmitsDiagnosticAndContinues()
    {
        AgentReadyEvent evt = new() { ProtocolVersion = "1.0.0", CoreVersion = "0.3.0" };
        TextReader reader = MakeReader(
            "not-json-at-all",
            "{\"broken\":",
            SerializeEvent(evt));

        List<string> diagnostics = [];
        CoreProcessRpcTransport transport = new(reader, new CapturingWriter(),
            onParseError: msg => diagnostics.Add(msg));

        List<Event> received = [];
        await foreach (Event e in transport.Events)
            received.Add(e);

        // Two malformed lines → two diagnostics.
        Assert.Equal(2, diagnostics.Count);
        // Stream still yielded the valid event after the bad lines.
        Event single = Assert.Single(received);
        Assert.IsType<AgentReadyEvent>(single);
    }

    [Fact]
    public async Task Events_MalformedLine_StreamDoesNotFault()
    {
        // If the stream faulted it would throw; just ensure we reach end-of-stream cleanly.
        TextReader reader = MakeReader(
            "{{{{{{{{{{",
            "definitely not JSON");
        CoreProcessRpcTransport transport = new(reader, new CapturingWriter());

        List<Event> received = [];
        Exception? thrown = null;
        try
        {
            await foreach (Event e in transport.Events)
                received.Add(e);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.Null(thrown);
        Assert.Empty(received);
    }

    // ---------------------------------------------------------------
    // 1.3 — Resilience: clean completion at end-of-stream
    // ---------------------------------------------------------------

    [Fact]
    public async Task Events_EndOfStream_CompletesNormally()
    {
        // Empty reader — StandardOutput closed immediately.
        TextReader reader = MakeReader();
        CoreProcessRpcTransport transport = new(reader, new CapturingWriter());

        List<Event> received = [];
        Exception? thrown = null;
        try
        {
            await foreach (Event e in transport.Events)
                received.Add(e);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.Null(thrown);
        Assert.Empty(received);
    }

    [Fact]
    public async Task Events_MultipleEvents_AllYielded_ThenCompletes()
    {
        AgentReadyEvent ready = new() { ProtocolVersion = "1.0.0", CoreVersion = "0.1.0" };
        AgentStartEvent start = new();
        TextReader reader = MakeReader(
            SerializeEvent(ready),
            SerializeEvent(start));
        CoreProcessRpcTransport transport = new(reader, new CapturingWriter());

        List<Event> received = [];
        await foreach (Event e in transport.Events)
            received.Add(e);

        Assert.Equal(2, received.Count);
        Assert.IsType<AgentReadyEvent>(received[0]);
        Assert.IsType<AgentStartEvent>(received[1]);
    }
}
