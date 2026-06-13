using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Dmon.Gateway;
using Dmon.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Drives the real <see cref="GatewayConnectionEndpoint"/> receive/dispatch loop over an
/// in-memory <see cref="WebSocket"/> (not just the gw-discriminator predicate). Proves the
/// negative: a control frame (<c>{"gw":"ping"}</c>) is handled locally and never written to
/// core stdin, while an ADR-003 frame is forwarded byte-unchanged.
/// </summary>
public sealed class ForwardingLoopTests
{
    [Fact]
    public async Task ControlFrame_DoesNotReachCore_WhileAdr003FrameDoes()
    {
        const string adr003 = """{"id":"req-1","type":"run","prompt":"hi"}""";
        const string ping = """{"gw":"ping"}""";

        CapturingWriter stdin = new();
        NeverReadingReader stdout = new();
        await using SessionHandler handler = new("loop-test", stdout, stdin);

        // Client sends: one ADR-003 command, then a ping, then closes.
        FakeClientWebSocket socket = new();
        socket.QueueText(adr003);
        socket.QueueText(ping);
        socket.QueueClose();

        RecordingConnection connection = new();
        GatewayConnectionEndpoint endpoint = new(
            new SessionRegistry(),
            new GatewayConnectionEndpoint.TestOptions(),
            NullLogger<GatewayConnectionEndpoint>.Instance);

        await endpoint.RunForwardingLoopForTestAsync(
            socket, connection, handler, CancellationToken.None);

        // The ADR-003 frame (and only it) was forwarded to core stdin, byte-unchanged.
        string written = stdin.GetWritten();
        Assert.Equal(adr003 + "\n", written);
        Assert.DoesNotContain("gw", written);

        // The ping was answered through the serialized connection funnel, not the core.
        Assert.Contains("""{"gw":"pong"}""", connection.Frames);
    }

    [Fact]
    public async Task NonStringId_DoesNotThrow_AndIsForwardedRawWithoutAck()
    {
        // A numeric id is a structurally-valid-but-wrong-typed frame from the untrusted boundary.
        // GetCommandId treats it as "no usable id": forward raw to core, do not ack, do not crash.
        const string numericId = """{"id":42,"type":"run","prompt":"hi"}""";

        CapturingWriter stdin = new();
        NeverReadingReader stdout = new();
        await using SessionHandler handler = new("numeric-id", stdout, stdin);

        FakeClientWebSocket socket = new();
        socket.QueueText(numericId);
        socket.QueueClose();

        RecordingConnection connection = new();
        GatewayConnectionEndpoint endpoint = NewEndpoint();

        await endpoint.RunForwardingLoopForTestAsync(
            socket, connection, handler, CancellationToken.None);

        Assert.Equal(numericId + "\n", stdin.GetWritten());
        Assert.DoesNotContain(connection.Frames, f => f.Contains("\"ack\""));
    }

    [Fact]
    public async Task NonStringGw_DoesNotThrow_AndIsTreatedAsNonControlFrame()
    {
        // {"gw":42} has a non-string discriminator. GetGwDiscriminator returns null, so the loop
        // treats it as an ADR-003 frame: it has no usable id either, so it is forwarded raw and
        // not acked. The point of the test is that this does not crash the loop.
        const string numericGw = """{"gw":42}""";

        CapturingWriter stdin = new();
        NeverReadingReader stdout = new();
        await using SessionHandler handler = new("numeric-gw", stdout, stdin);

        FakeClientWebSocket socket = new();
        socket.QueueText(numericGw);
        socket.QueueClose();

        RecordingConnection connection = new();
        GatewayConnectionEndpoint endpoint = NewEndpoint();

        // No uncaught exception escapes the loop.
        await endpoint.RunForwardingLoopForTestAsync(
            socket, connection, handler, CancellationToken.None);

        Assert.Equal(numericGw + "\n", stdin.GetWritten());
        Assert.DoesNotContain(connection.Frames, f => f.Contains("\"ack\""));
    }

    [Fact]
    public async Task NoIdCommandFrame_IsForwardedToCore_AndNotAcked()
    {
        // N1: a command frame with no "id" is forwarded byte-unchanged so the core can emit an
        // error, but cannot be acked (no id to ack).
        const string noId = """{"type":"run","prompt":"hi"}""";

        CapturingWriter stdin = new();
        NeverReadingReader stdout = new();
        await using SessionHandler handler = new("no-id", stdout, stdin);

        FakeClientWebSocket socket = new();
        socket.QueueText(noId);
        socket.QueueClose();

        RecordingConnection connection = new();
        GatewayConnectionEndpoint endpoint = NewEndpoint();

        await endpoint.RunForwardingLoopForTestAsync(
            socket, connection, handler, CancellationToken.None);

        Assert.Equal(noId + "\n", stdin.GetWritten());
        Assert.DoesNotContain(connection.Frames, f => f.Contains("\"ack\""));
    }

    [Fact]
    public async Task CoreWriteFailure_RemovesAdmission_DoesNotAck_AndTerminatesCleanly()
    {
        // H1: on a core-write failure the admission must be compensated (so a resend retries),
        // no ack sent (no false acceptance), and the loop must terminate without an uncaught
        // exception. The first stdin throws on write; a subsequent resend of the same id must be
        // re-forwarded to a working stdin.
        const string command = """{"id":"req-1","type":"run","prompt":"hi"}""";

        // Throws on the first write (dead stdin), succeeds and captures on subsequent writes.
        FailFirstWriter stdin = new();
        NeverReadingReader stdout = new();
        await using SessionHandler handler = new("write-fail", stdout, stdin);

        // First connection: command fails to reach the core.
        FakeClientWebSocket firstSocket = new();
        firstSocket.QueueText(command);
        firstSocket.QueueClose();

        RecordingConnection firstConnection = new();
        GatewayConnectionEndpoint endpoint = NewEndpoint();

        // (c) The loop terminates without an uncaught exception (a propagated exception would
        // fail the test).
        await endpoint.RunForwardingLoopForTestAsync(
            firstSocket, firstConnection, handler, CancellationToken.None);

        // (a) No ack was sent for the failed write.
        Assert.DoesNotContain(firstConnection.Frames, f => f.Contains("\"ack\""));
        Assert.Equal("", stdin.GetWritten());

        // (b) The admission was removed, so a resend of the same id is re-forwarded to the (now
        // working) stdin and acked — not swallowed as a duplicate.
        FakeClientWebSocket resendSocket = new();
        resendSocket.QueueText(command);
        resendSocket.QueueClose();

        RecordingConnection resendConnection = new();
        await endpoint.RunForwardingLoopForTestAsync(
            resendSocket, resendConnection, handler, CancellationToken.None);

        Assert.Equal(command + "\n", stdin.GetWritten());
        Assert.Contains(resendConnection.Frames, f => f.Contains("\"ack\"") && f.Contains("req-1"));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GatewayConnectionEndpoint NewEndpoint() =>
        new(new SessionRegistry(),
            new GatewayConnectionEndpoint.TestOptions(),
            NullLogger<GatewayConnectionEndpoint>.Instance);

    /// <summary>
    /// A stdin writer that throws on its first write (simulating a transiently-dead core stdin),
    /// then succeeds and captures on every subsequent write (the resend reaches a working stdin).
    /// </summary>
    private sealed class FailFirstWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();
        private bool _failed;

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("core stdin closed");
            }

            _sb.Append(buffer.Span);
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public string GetWritten() => _sb.ToString();
    }

    private sealed class RecordingConnection : IGatewayConnection
    {
        private readonly List<string> _frames = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Frames
        {
            get { lock (_gate) { return [.. _frames]; } }
        }

        public ValueTask SendAsync(string frame, CancellationToken cancellationToken)
        {
            lock (_gate) { _frames.Add(frame); }
            return ValueTask.CompletedTask;
        }

        public void Abort() { }
    }

    private sealed class CapturingWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            _sb.Append(buffer.Span);
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public string GetWritten() => _sb.ToString();
    }

    /// <summary>A stdout reader that never yields a line, so the pump stays idle.</summary>
    private sealed class NeverReadingReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            return null;
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }

    /// <summary>
    /// Minimal in-memory <see cref="WebSocket"/> that replays a queued script of inbound
    /// messages to the receive loop. Outbound sends are accepted and discarded (the loop's
    /// pong is asserted via the <see cref="IGatewayConnection"/> instead).
    /// </summary>
    private sealed class FakeClientWebSocket : WebSocket
    {
        private readonly Channel<(WebSocketMessageType Type, byte[] Payload)> _inbound =
            Channel.CreateUnbounded<(WebSocketMessageType, byte[])>();

        private WebSocketState _state = WebSocketState.Open;

        public void QueueText(string frame) =>
            _inbound.Writer.TryWrite((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(frame)));

        public void QueueClose() =>
            _inbound.Writer.TryWrite((WebSocketMessageType.Close, []));

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            (WebSocketMessageType type, byte[] payload) =
                await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            int count = Math.Min(payload.Length, buffer.Count);
            Array.Copy(payload, 0, buffer.Array!, buffer.Offset, count);
            return new WebSocketReceiveResult(count, type, endOfMessage: true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;

        public override void Dispose() { }
    }
}
