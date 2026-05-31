using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Dmon.Gateway;
using Dmon.Gateway.Protocol;
using Dmon.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Verifies Group 5 command idempotency: ack-by-id and dedupe-by-id across the forwarding loop.
///
/// Each test drives <see cref="GatewayConnectionEndpoint.RunForwardingLoopForTestAsync"/> over
/// an in-memory <see cref="WebSocket"/> seam and asserts on both core-stdin writes and the
/// frames sent back through the <see cref="IGatewayConnection"/> recording.
/// </summary>
public sealed class CommandIdempotencyTests
{
    // -------------------------------------------------------------------------
    // 5.1 — Accepted command is forwarded once and acked once
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AcceptedCommand_ForwardedOnce_AndAckedOnce()
    {
        const string cmd = """{"id":"req-1","type":"run","prompt":"hello"}""";

        CapturingWriter stdin = new();
        await using SessionHandler handler = new("s1", new NeverReadingReader(), stdin);

        FakeSocket socket = new();
        socket.QueueText(cmd);
        socket.QueueClose();

        RecordingConnection connection = new();
        GatewayConnectionEndpoint endpoint = MakeEndpoint();

        await endpoint.RunForwardingLoopForTestAsync(socket, connection, handler, CancellationToken.None);

        // Forwarded byte-unchanged exactly once (ADR-003 framing adds "\n").
        Assert.Equal(cmd + "\n", stdin.GetWritten());

        // Ack sent with the correct id exactly once.
        string expectedAck = ControlFrameSerializer.Serialize(new AckFrame { Id = "req-1" });
        Assert.Single(connection.Frames, f => f == expectedAck);
    }

    // -------------------------------------------------------------------------
    // 5.2 — Resent command (same id) is not forwarded again; re-ack is sent
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ResentCommand_NotForwardedAgain_ReAcked()
    {
        const string cmd = """{"id":"dup-1","type":"run","prompt":"hello"}""";

        CapturingWriter stdin = new();
        await using SessionHandler handler = new("s2", new NeverReadingReader(), stdin);

        FakeSocket socket = new();
        socket.QueueText(cmd);
        socket.QueueText(cmd); // resend same id
        socket.QueueClose();

        RecordingConnection connection = new();
        GatewayConnectionEndpoint endpoint = MakeEndpoint();

        await endpoint.RunForwardingLoopForTestAsync(socket, connection, handler, CancellationToken.None);

        // Core receives the frame exactly once despite two arrivals.
        string written = stdin.GetWritten();
        Assert.Equal(cmd + "\n", written);

        // Ack sent twice — once for the original, once as re-ack for the duplicate.
        string expectedAck = ControlFrameSerializer.Serialize(new AckFrame { Id = "dup-1" });
        int ackCount = connection.Frames.Count(f => f == expectedAck);
        Assert.Equal(2, ackCount);
    }

    // -------------------------------------------------------------------------
    // Distinct ids — both forwarded and both acked
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DistinctIds_BothForwarded_BothAcked()
    {
        const string cmd1 = """{"id":"a","type":"run","prompt":"one"}""";
        const string cmd2 = """{"id":"b","type":"run","prompt":"two"}""";

        CapturingWriter stdin = new();
        await using SessionHandler handler = new("s3", new NeverReadingReader(), stdin);

        FakeSocket socket = new();
        socket.QueueText(cmd1);
        socket.QueueText(cmd2);
        socket.QueueClose();

        RecordingConnection connection = new();
        GatewayConnectionEndpoint endpoint = MakeEndpoint();

        await endpoint.RunForwardingLoopForTestAsync(socket, connection, handler, CancellationToken.None);

        // Both frames reach the core.
        string written = stdin.GetWritten();
        Assert.Contains(cmd1 + "\n", written);
        Assert.Contains(cmd2 + "\n", written);

        // Both acks sent.
        string ackA = ControlFrameSerializer.Serialize(new AckFrame { Id = "a" });
        string ackB = ControlFrameSerializer.Serialize(new AckFrame { Id = "b" });
        Assert.Contains(ackA, connection.Frames);
        Assert.Contains(ackB, connection.Frames);
    }

    // -------------------------------------------------------------------------
    // Dedupe survives across attach cycles (reconnect scenario)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Dedupe_SurvivesReattach_SameIdNotForwardedAgain()
    {
        const string cmd = """{"id":"cross-1","type":"run","prompt":"hi"}""";

        CapturingWriter stdin = new();
        await using SessionHandler handler = new("s4", new NeverReadingReader(), stdin);

        // --- First connection: admit the command ---
        FakeSocket socket1 = new();
        socket1.QueueText(cmd);
        socket1.QueueClose();

        RecordingConnection conn1 = new();
        GatewayConnectionEndpoint endpoint = MakeEndpoint();
        await endpoint.RunForwardingLoopForTestAsync(socket1, conn1, handler, CancellationToken.None);

        // Core received the frame once.
        Assert.Equal(cmd + "\n", stdin.GetWritten());

        // --- Second connection (simulated reattach): resend same id ---
        stdin.Reset();
        FakeSocket socket2 = new();
        socket2.QueueText(cmd); // resend same id after reconnect
        socket2.QueueClose();

        RecordingConnection conn2 = new();
        await endpoint.RunForwardingLoopForTestAsync(socket2, conn2, handler, CancellationToken.None);

        // Core must NOT receive it again — dedupe set lives on the handler, not the connection.
        Assert.Equal(string.Empty, stdin.GetWritten());

        // Re-ack was sent on the second connection.
        string expectedAck = ControlFrameSerializer.Serialize(new AckFrame { Id = "cross-1" });
        Assert.Contains(expectedAck, conn2.Frames);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GatewayConnectionEndpoint MakeEndpoint() =>
        new(new SessionRegistry(), NullLogger<GatewayConnectionEndpoint>.Instance);

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

        public void Reset() => _sb.Clear();
    }

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

    private sealed class FakeSocket : WebSocket
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
