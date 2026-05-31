using System.Net.WebSockets;
using System.Text;
using Dmon.Gateway.Protocol;
using Dmon.Gateway.Sessions;

namespace Dmon.Gateway;

/// <summary>
/// Handles the WebSocket connection-control loop for the /ws endpoint.
///
/// Protocol flow:
///   1. First frame must be <c>attach</c>. Anything else → close with an error status.
///   2. On <c>attach {sessionId, lastSeq}</c>: look up the session in the registry.
///      - Unknown sessionId → close with 4404. Session creation is Group 10.
///      - Known: wrap the socket in <see cref="WebSocketGatewayConnection"/>, call
///        <see cref="SessionHandler.Attach"/>, reply <c>attached {generation, headSeq}</c>.
///   3. Forwarding loop:
///      - Frame with no "gw" field → ADR-003 command → forward byte-unchanged to core stdin.
///      - "gw":"ping" → reply "gw":"pong".
///      - "gw":"pong" → accepted, no-op (heartbeat reply from client).
///      - Any other "gw" value → ignore (unknown future control frame).
///   4. On socket close / error / cancellation → Detach the handler so the core survives.
///
/// Generation semantics: Attach() returns a strictly-increasing generation. The attached
/// reply carries it so the client can detect reconnections. Fencing (dropping older-gen
/// frames, evicting the prior connection) is Group 6 — not implemented here.
///
/// Replay semantics: lastSeq is accepted in the attach frame but replay of
/// (lastSeq, headSeq] is Group 4 — not implemented here.
/// </summary>
public sealed class GatewayConnectionEndpoint
{
    private readonly SessionRegistry _registry;
    private readonly ILogger<GatewayConnectionEndpoint> _logger;

    // Receive buffer size; fits any typical JSONL command line.
    private const int ReceiveBufferSize = 64 * 1024;

    // Ceiling on a single inbound message. A client that never sets EndOfMessage would
    // otherwise grow the accumulation buffer until OOM. 4 MiB comfortably exceeds any
    // ADR-003 command/event frame; anything larger is treated as abuse and closes the socket.
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    // WebSocket close status 1009: message too big.
    private const WebSocketCloseStatus MessageTooBig = (WebSocketCloseStatus)1009;

    public GatewayConnectionEndpoint(
        SessionRegistry registry,
        ILogger<GatewayConnectionEndpoint> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using WebSocket socket = await context.WebSockets
            .AcceptWebSocketAsync()
            .ConfigureAwait(false);

        CancellationToken ct = context.RequestAborted;

        // --- Phase 1: Attach handshake ---
        string? firstRaw;
        try
        {
            firstRaw = await ReceiveTextFrameAsync(socket, ct).ConfigureAwait(false);
        }
        catch (ProtocolViolationFrameException ex)
        {
            await CloseOnViolationAsync(socket, ex, ct).ConfigureAwait(false);
            return;
        }

        if (firstRaw is null)
        {
            // Socket closed before sending anything.
            return;
        }

        string? gw = ControlFrameSerializer.GetGwDiscriminator(firstRaw);
        if (gw != "attach")
        {
            _logger.LogWarning("First frame is not attach (gw={Gw}); closing.", gw);
            await socket.CloseAsync(
                (WebSocketCloseStatus)4400,
                "first frame must be attach",
                ct).ConfigureAwait(false);
            return;
        }

        AttachFrame? attachFrame = ControlFrameSerializer.ParseAttach(firstRaw);
        if (attachFrame is null)
        {
            await socket.CloseAsync(
                (WebSocketCloseStatus)4400,
                "malformed attach frame",
                ct).ConfigureAwait(false);
            return;
        }

        SessionHandler? handler = _registry.TryGet(attachFrame.SessionId);
        if (handler is null)
        {
            // Session creation is Group 10. Unknown sessions are rejected here.
            _logger.LogWarning(
                "Attach for unknown session {SessionId}; closing. Session creation is Group 10.",
                attachFrame.SessionId);
            await socket.CloseAsync(
                (WebSocketCloseStatus)4404,
                $"session '{attachFrame.SessionId}' not found",
                ct).ConfigureAwait(false);
            return;
        }

        // lastSeq is captured here; replay of (lastSeq, headSeq] is Group 4.
        _ = attachFrame.LastSeq;

        using WebSocketGatewayConnection connection = new(socket);
        long generation = handler.Attach(connection);

        AttachedFrame attachedFrame = new()
        {
            Generation = generation,
            HeadSeq = handler.HeadSeq,
        };
        // Route the reply through the connection's serialized send funnel, not the raw socket:
        // Attach() has already released the pump's wake, so the first buffered event may be
        // draining concurrently on the same socket.
        await connection.SendAsync(
            ControlFrameSerializer.Serialize(attachedFrame), ct).ConfigureAwait(false);

        // --- Phase 2: Forwarding loop ---
        try
        {
            await RunForwardingLoopAsync(socket, connection, handler, ct).ConfigureAwait(false);
        }
        catch (ProtocolViolationFrameException ex)
        {
            // Detach first so the pump stops targeting this connection before we close it.
            handler.Detach();
            await CloseOnViolationAsync(socket, ex, ct).ConfigureAwait(false);
            return;
        }
        finally
        {
            // Core and handler survive the socket disconnect.
            handler.Detach();
        }
    }

    /// <summary>
    /// Test seam: drives the real receive/dispatch loop over an arbitrary <see cref="WebSocket"/>,
    /// so tests can assert that control frames never reach core stdin while ADR-003 frames do —
    /// exercising the loop itself, not just the routing predicate.
    /// </summary>
    internal Task RunForwardingLoopForTestAsync(
        WebSocket socket,
        IGatewayConnection connection,
        SessionHandler handler,
        CancellationToken cancellationToken) =>
        RunForwardingLoopAsync(socket, connection, handler, cancellationToken);

    private async Task RunForwardingLoopAsync(
        WebSocket socket,
        IGatewayConnection connection,
        SessionHandler handler,
        CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open)
        {
            string? raw = await ReceiveTextFrameAsync(socket, cancellationToken)
                .ConfigureAwait(false);
            if (raw is null)
                break;

            string? gw = ControlFrameSerializer.GetGwDiscriminator(raw);

            if (gw is null)
            {
                // ADR-003 command — forward byte-unchanged.
                await handler.WriteToCoreAsync(raw, cancellationToken).ConfigureAwait(false);
                continue;
            }

            switch (gw)
            {
                case "ping":
                    // Route through the same serialized funnel as drained events.
                    await connection.SendAsync(
                        ControlFrameSerializer.Serialize(new PongFrame()), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case "pong":
                    // Heartbeat reply from client — accepted, no-op. Missed-heartbeat
                    // detection is Group 7.
                    break;

                default:
                    // Unknown control frame — ignore for forward compatibility.
                    _logger.LogDebug("Ignoring unknown control frame gw={Gw}.", gw);
                    break;
            }
        }
    }

    /// <summary>
    /// Receives one complete WebSocket text message, accumulating continuation frames.
    /// Returns <c>null</c> on a clean close, cancellation, or transport error.
    /// Throws <see cref="ProtocolViolationFrameException"/> when the client sends a binary
    /// message (ADR-003 is text JSONL) or exceeds <see cref="MaxMessageBytes"/>; the caller
    /// closes the socket. The size cap prevents a client that never sets EndOfMessage from
    /// growing the accumulation buffer until OOM.
    /// </summary>
    private static async Task<string?> ReceiveTextFrameAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ReceiveBufferSize];
        using MemoryStream ms = new();

        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // ADR-003 is text JSONL; a binary message is a protocol violation.
                throw new ProtocolViolationFrameException(
                    (WebSocketCloseStatus)4400, "binary frames are not supported");
            }

            if (ms.Length + result.Count > MaxMessageBytes)
            {
                throw new ProtocolViolationFrameException(
                    MessageTooBig, $"message exceeds {MaxMessageBytes} bytes");
            }

            ms.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private async Task CloseOnViolationAsync(
        WebSocket socket,
        ProtocolViolationFrameException violation,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Closing connection: {Reason}", violation.Reason);
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(violation.CloseStatus, violation.Reason, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Peer already gone; nothing more to do.
            }
            catch (OperationCanceledException)
            {
                // Request aborted; nothing more to do.
            }
        }
    }

    /// <summary>
    /// Signals an inbound frame that violates the protocol (binary message, or a message
    /// larger than <see cref="MaxMessageBytes"/>). Carries the close status the caller uses
    /// to abort the connection.
    /// </summary>
    private sealed class ProtocolViolationFrameException : Exception
    {
        public ProtocolViolationFrameException(WebSocketCloseStatus closeStatus, string reason)
            : base(reason)
        {
            CloseStatus = closeStatus;
            Reason = reason;
        }

        public WebSocketCloseStatus CloseStatus { get; }

        public string Reason { get; }
    }
}
