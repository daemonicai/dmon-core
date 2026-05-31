using System.Net.WebSockets;
using System.Text;
using Dmon.Gateway.Protocol;
using Dmon.Gateway.Sessions;
using Microsoft.Extensions.Options;

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
///      - "gw":"pong" → heartbeat reply — records last inbound activity timestamp.
///      - Any other "gw" value → ignore (unknown future control frame).
///      - A concurrent heartbeat task sends gateway→client "gw":"ping" on
///        <see cref="GatewayOptions.HeartbeatIntervalSeconds"/>; if no frame arrives within
///        2× the interval the connection is treated as dead and the loop exits.
///   4. On socket close / error / cancellation → Detach the handler so the core survives.
///
/// Generation semantics: Attach() returns a strictly-increasing generation. The attached
/// reply carries it so the client can detect reconnections. Before acting on each inbound
/// frame the loop checks whether this connection is still current; if a newer attach has
/// superseded it, the frame is dropped, the connection is closed with status 4409 (conflict),
/// and the loop exits (Group 6 / 6.2).
///
/// Replay semantics: lastSeq from the attach frame is passed to SessionHandler.Attach, which
/// sets the delivery cursor. The single drain loop replays (lastSeq, headSeq] before delivering
/// live events. The accurate headSeq is carried in the attached reply (ADR-014).
/// </summary>
public sealed class GatewayConnectionEndpoint
{
    private readonly SessionRegistry _registry;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _timeProvider;
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
        IOptionsMonitor<GatewayOptions> options,
        TimeProvider timeProvider,
        ILogger<GatewayConnectionEndpoint> logger)
    {
        _registry = registry;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Test constructor: uses a fixed <see cref="GatewayOptions"/> snapshot and
    /// <see cref="TimeProvider.System"/> so legacy tests that only supply a registry and
    /// logger continue to compile without modification.
    /// </summary>
    internal GatewayConnectionEndpoint(
        SessionRegistry registry,
        ILogger<GatewayConnectionEndpoint> logger)
        : this(registry, new StaticOptionsMonitor(new GatewayOptions()), TimeProvider.System, logger)
    {
    }

    /// <summary>Minimal <see cref="IOptionsMonitor{T}"/> for test / default construction.</summary>
    private sealed class StaticOptionsMonitor : IOptionsMonitor<GatewayOptions>
    {
        private readonly GatewayOptions _value;

        public StaticOptionsMonitor(GatewayOptions value) => _value = value;

        public GatewayOptions CurrentValue => _value;

        public GatewayOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    public async Task HandleAsync(HttpContext context)
    {
        // 9.2 — Shared-key check: runs before IsWebSocketRequest so an unauthorized caller
        // learns nothing about the endpoint (no upgrade attempted, no socket opened).
        string? authHeader = context.Request.Headers.Authorization;
        if (!SharedKeyAuthenticator.IsAuthorized(authHeader, _options.CurrentValue.SharedKey))
        {
            _logger.LogWarning("WebSocket upgrade rejected: missing or mismatched Authorization header.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

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

        using WebSocketGatewayConnection connection = new(socket);
        AttachResult attachResult = handler.Attach(connection, attachFrame.LastSeq);

        AttachedFrame attachedFrame = new()
        {
            Generation = attachResult.Generation,
            HeadSeq = attachResult.HeadSeq,
        };
        // Route the reply through the connection's serialized send funnel, not the raw socket:
        // Attach() has already released the pump's wake, so the first buffered event may be
        // draining concurrently on the same socket.
        await connection.SendAsync(
            ControlFrameSerializer.Serialize(attachedFrame), ct).ConfigureAwait(false);

        // --- Phase 2: Forwarding loop ---
        try
        {
            // Production always enforces fencing and enables heartbeats.
            await RunForwardingLoopAsync(
                socket, connection, handler, attachResult.Generation,
                enforceFencing: true, enableHeartbeat: true, ct)
                .ConfigureAwait(false);
        }
        catch (ProtocolViolationFrameException ex)
        {
            // Detach first so the pump stops targeting this connection before we close it.
            handler.Detach(connection);
            await CloseOnViolationAsync(socket, ex, ct).ConfigureAwait(false);
            return;
        }
        finally
        {
            // Core and handler survive the socket disconnect.
            handler.Detach(connection);
        }
    }

    /// <summary>
    /// Test seam: drives the real receive/dispatch loop over an arbitrary <see cref="WebSocket"/>,
    /// so tests can assert that control frames never reach core stdin while ADR-003 frames do —
    /// exercising the loop itself, not just the routing predicate.
    /// </summary>
    /// <param name="myGeneration">
    /// The generation assigned to <paramref name="connection"/> at attach time. Only consulted
    /// when <paramref name="enforceFencing"/> is <c>true</c>.
    /// </param>
    /// <param name="enforceFencing">
    /// When <c>true</c>, the per-frame gate compares <paramref name="myGeneration"/> against the
    /// handler's current generation and closes a superseded connection. Legacy tests that do not
    /// exercise fencing pass <c>false</c> explicitly. Production always passes <c>true</c>.
    /// </param>
    /// <param name="enableHeartbeat">
    /// When <c>true</c>, the heartbeat task runs concurrently. Legacy forwarding-loop tests pass
    /// <c>false</c> to avoid the heartbeat timer firing unexpectedly during short-lived test runs.
    /// Production always passes <c>true</c> (via <see cref="HandleAsync"/>).
    /// </param>
    internal Task RunForwardingLoopForTestAsync(
        WebSocket socket,
        IGatewayConnection connection,
        SessionHandler handler,
        CancellationToken cancellationToken,
        long myGeneration = 0,
        bool enforceFencing = false,
        bool enableHeartbeat = false) =>
        RunForwardingLoopAsync(
            socket, connection, handler, myGeneration, enforceFencing, enableHeartbeat,
            cancellationToken);

    // WebSocket close status 4409: connection conflict — this connection has been superseded
    // by a newer attach. Chosen from the application-range (4000–4999).
    private const WebSocketCloseStatus FencedConnectionStatus = (WebSocketCloseStatus)4409;

    private async Task RunForwardingLoopAsync(
        WebSocket socket,
        IGatewayConnection connection,
        SessionHandler handler,
        long myGeneration,
        bool enforceFencing,
        bool enableHeartbeat,
        CancellationToken cancellationToken)
    {
        // Shared activity state: a single-element array so both the forwarding loop and the
        // heartbeat async method can share a reference to the same long cell. Volatile
        // write/read suffices — exact ordering does not matter here; the worst outcome is
        // a single false-positive missed-beat.
        long[] lastActivityTicks = [_timeProvider.GetTimestamp()];

        // Linked CTS: the heartbeat task cancels this on a missed beat, which propagates to
        // ReceiveTextFrameAsync and exits the forwarding loop cleanly (detected disconnect).
        using CancellationTokenSource heartbeatCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task heartbeatTask = enableHeartbeat
            ? RunHeartbeatAsync(connection, heartbeatCts, lastActivityTicks, cancellationToken)
            : Task.CompletedTask;

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                string? raw = await ReceiveTextFrameAsync(socket, heartbeatCts.Token)
                    .ConfigureAwait(false);
                if (raw is null)
                    break;

                // Record inbound activity (any frame keeps the connection alive).
                Volatile.Write(ref lastActivityTicks[0], _timeProvider.GetTimestamp());

                // Per-frame generation gate (Group 6 / 6.2): check under the handler's lock so the
                // read is consistent with Attach's write. myGeneration can only be older (a newer
                // attach bumped _generation); if this connection has been superseded, drop the frame,
                // close the socket, and exit. The gate fires for every frame type — command, ping,
                // pong, unknown — so a fenced connection writes nothing further. Enforcement is gated
                // by an explicit flag, not by the generation value: production always enforces; only
                // legacy tests that do not exercise fencing opt out.
                if (enforceFencing && myGeneration != handler.CurrentGeneration)
                {
                    await CloseOnViolationAsync(
                        socket,
                        new ProtocolViolationFrameException(
                            FencedConnectionStatus,
                            "connection superseded by a newer attach"),
                        cancellationToken).ConfigureAwait(false);
                    break;
                }

                string? gw = ControlFrameSerializer.GetGwDiscriminator(raw);

                if (gw is null)
                {
                    // ADR-003 command — forward byte-unchanged.
                    string? commandId = ControlFrameSerializer.GetCommandId(raw);

                    if (commandId is not null)
                    {
                        // Admit-then-forward: id recorded under lock before the write so a
                        // concurrent duplicate sees Duplicate even if the first write is mid-flight.
                        CommandAdmission admission = handler.TryAdmitCommand(commandId);
                        if (admission == CommandAdmission.Accepted)
                        {
                            try
                            {
                                await handler.WriteToCoreAsync(raw, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // The core write failed (dead/closed stdin). Compensate the admission
                                // so the client's resend is re-forwarded rather than swallowed as a
                                // duplicate, and do NOT ack — a false ack would tell the client the
                                // turn was accepted when the core never saw it (ADR-012 Decision 5).
                                // The connection cannot proceed without a working core, so close it
                                // cleanly and break; the HandleAsync finally detaches the handler.
                                handler.RemoveAdmission(commandId);
                                await CloseOnViolationAsync(
                                    socket,
                                    new ProtocolViolationFrameException(
                                        (WebSocketCloseStatus)4500, "core write failed"),
                                    cancellationToken).ConfigureAwait(false);
                                break;
                            }

                            // Clear the outstanding-request entry on a successful write: the client
                            // has answered the permission gate and the core has received the response,
                            // so the parked request no longer needs to be re-surfaced on reattach.
                            // Only clear after the write succeeds — a failed write must leave the
                            // entry intact so a retry can re-surface the prompt on the next attach.
                            string? commandType = ControlFrameSerializer.GetCommandType(raw);
                            if (commandType is "tool.confirmResponse" or "ui.inputResponse")
                                handler.ClearOutstandingRequest(commandId);

                            // Ack only after a successful write, so the ack always implies the core
                            // received the command.
                            await connection.SendAsync(
                                ControlFrameSerializer.Serialize(new AckFrame { Id = commandId }),
                                cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // Duplicate: the command was already admitted (and, on the happy path,
                            // already written). Re-ack so a client that missed the first ack learns
                            // the command was received, without writing it to the core twice.
                            await connection.SendAsync(
                                ControlFrameSerializer.Serialize(new AckFrame { Id = commandId }),
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        // Malformed: no usable id. ADR-003 requires id, so this is defensive.
                        // Forward unchanged so the core can emit an error response; we cannot ack
                        // without an id, so we do not send one.
                        await handler.WriteToCoreAsync(raw, cancellationToken).ConfigureAwait(false);
                    }

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
                        // Heartbeat reply — activity already recorded above. No further action.
                        break;

                    default:
                        // Unknown control frame — ignore for forward compatibility.
                        _logger.LogDebug("Ignoring unknown control frame gw={Gw}.", gw);
                        break;
                }
            }
        }
        finally
        {
            // Signal the heartbeat task to stop and wait for it to exit cleanly.
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    /// <summary>
    /// Sends periodic "gw":"ping" frames and detects missed beats.
    ///
    /// On each interval: sends a ping through the serialized send funnel. After sending, waits
    /// one more interval; if no frame has arrived from the client within that window (i.e.
    /// <paramref name="lastActivityTicks"/> has not advanced past the pre-ping snapshot),
    /// cancels <paramref name="heartbeatCts"/> so the forwarding loop's ReceiveAsync exits and
    /// the handler is detached, starting the grace timer.
    ///
    /// All sends go through <paramref name="connection"/> (the serialized funnel) — never the
    /// raw socket. The task exits cleanly on <paramref name="loopCancellationToken"/> cancellation.
    /// </summary>
    private async Task RunHeartbeatAsync(
        IGatewayConnection connection,
        CancellationTokenSource heartbeatCts,
        long[] lastActivityTicks,
        CancellationToken loopCancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(_options.CurrentValue.HeartbeatIntervalSeconds);
        // Missed-beat deadline: 2× the interval. If no frame arrives from the client within
        // this window after the ping was sent, the connection is treated as dead.
        TimeSpan deadline = interval + interval;

        try
        {
            while (!loopCancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, _timeProvider, loopCancellationToken).ConfigureAwait(false);

                // Snapshot the activity counter before sending the ping. A pong (or any other
                // frame) arriving after the ping will update lastActivityTicks[0] past this value,
                // proving the client is alive.
                long activityBeforePing = Volatile.Read(ref lastActivityTicks[0]);

                try
                {
                    await connection.SendAsync(
                        ControlFrameSerializer.Serialize(new PingFrame()),
                        loopCancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Send funnel threw — connection is already broken; cancel the loop.
                    _logger.LogDebug("Heartbeat send failed; treating as dead connection: {Reason}", ex.Message);
                    await heartbeatCts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                // Wait one deadline window for any response.
                await Task.Delay(deadline, _timeProvider, loopCancellationToken).ConfigureAwait(false);

                long activityAfterWindow = Volatile.Read(ref lastActivityTicks[0]);
                if (activityAfterWindow == activityBeforePing)
                {
                    // No frame arrived in the window — treat as detected disconnect.
                    _logger.LogInformation(
                        "Missed heartbeat (no frame in {Deadline}); detaching connection.",
                        deadline);
                    await heartbeatCts.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit: loop was cancelled (either by the outer CT or by heartbeatCts itself).
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
    /// Signals that the connection cannot proceed and must be closed: an inbound frame that
    /// violates the protocol (binary message, or a message larger than
    /// <see cref="MaxMessageBytes"/>), or a failed core write. Carries the close status the
    /// caller uses to abort the connection cleanly.
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
