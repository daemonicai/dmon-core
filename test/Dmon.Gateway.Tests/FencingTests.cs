using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Dmon.Gateway;
using Dmon.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Group 6: fencing and single active writer — per-frame generation gate.
///
/// These tests exercise the path where a frame arrives on a connection whose generation is
/// older than the handler's current generation (the eviction window case). The frame must not
/// be forwarded to the core and the socket must be closed with status 4409.
/// </summary>
public sealed class FencingTests
{
    /// <summary>
    /// 6.2 — A frame arriving on a fenced connection (older generation) is not forwarded to the
    /// core and the connection is closed with status 4409.
    ///
    /// Scenario: connA attaches (gen G); connB attaches (gen G+1), superseding connA.
    /// A frame then arrives on connA's forwarding loop (still running in the eviction window).
    /// The loop detects myGeneration (G) != CurrentGeneration (G+1), drops the frame, and closes.
    /// </summary>
    [Fact]
    public async Task FencedFrame_IsNotForwarded_AndSocketIsClosed()
    {
        const string command = """{"id":"req-fenced","type":"run","prompt":"should not reach core"}""";

        CapturingWriter stdin = new();
        NeverReadingReader stdout = new();
        await using SessionHandler handler = new("fence-test", stdout, stdin);

        // connA attaches first; capture its generation.
        RecordingConnection connA = new();
        AttachResult resultA = handler.Attach(connA, lastSeq: 0);

        // connB supersedes connA; now CurrentGeneration > resultA.Generation.
        RecordingConnection connB = new();
        handler.Attach(connB, lastSeq: 0);

        // Drive connA's forwarding loop with its own (now stale) generation.
        // The loop should detect the fencing, not forward the frame, and close the socket.
        FakeClientWebSocket socketA = new();
        socketA.QueueText(command);
        socketA.QueueClose(); // fallback so the loop always terminates

        GatewayConnectionEndpoint endpoint = NewEndpoint();
        await endpoint.RunForwardingLoopForTestAsync(
            socketA, connA, handler, CancellationToken.None,
            myGeneration: resultA.Generation, enforceFencing: true);

        // The fenced command must NOT have reached core stdin.
        Assert.Equal("", stdin.GetWritten());

        // The socket must have been closed (Aborted or Closed state after fencing).
        Assert.True(
            socketA.State is WebSocketState.Closed or WebSocketState.Aborted,
            $"Expected socket closed after fencing; state was {socketA.State}");

        // connA must not have been sent any frame by the loop (no ack, no pong, nothing).
        Assert.DoesNotContain(connA.Frames, f => f.Contains("ack") || f.Contains("pong"));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GatewayConnectionEndpoint NewEndpoint() =>
        new(new SessionRegistry(),
            new GatewayConnectionEndpoint.TestOptions(),
            NullLogger<GatewayConnectionEndpoint>.Instance);

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
    /// messages. Tracks close/abort state for assertions.
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
