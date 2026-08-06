import Foundation
import os

private let logger = Logger(subsystem: "ai.daemonic.dmon-home", category: "GatewayConnection")

/// One item the read loop hands to its consumer: a recognised control
/// frame other than `ping` (the loop answers `ping` itself — see
/// `GatewayConnection`), or a raw ADR-003 event, carried unchanged.
///
/// `.unrecognizedControl` is deliberately absent here — it is logged and
/// skipped by the read loop, never surfaced, so it can never be mistaken
/// for an `.event` and corrupt the sequence count section 7.3 builds on
/// top of this stream.
public enum GatewayInboundItem: Hashable, Sendable {
    case control(GatewayControlFrame)
    case event(String)
}

/// Owns a `GatewayTransport` and the `ControlFrameCodec`, and runs the
/// single inbound read loop where the two meet.
///
/// The read loop answers `ping` with `pong` itself, without ever routing
/// the reply through the consumer: `send(_:)` for the `pong` is called
/// directly from the loop, so a consumer that never drains the stream
/// `connect()` returns cannot delay — or prevent — a heartbeat reply. That
/// matters because the network host (`NetworkConnectionEndpoint`) reaps a
/// connection that produces no frame of any kind within 2x its heartbeat
/// interval; a read loop whose liveness depended on the consumer would
/// make an application-level stall indistinguishable from a dead
/// connection.
///
/// Every other recognised control frame, and every raw event, is handed to
/// the consumer through an `AsyncThrowingStream` (see `connect()`).
/// `AsyncThrowingStream.Continuation.yield` is not `async` and never
/// suspends under *any* buffering policy (SE-0314) — so no policy choice
/// here can stall the loop; that guarantee is a property of the type, not
/// of the `.unbounded` policy this actor picks. The policy is `.unbounded`
/// for a different reason: **the read loop itself never drops a frame it
/// has decoded and is about to yield.** A bounded policy would drop
/// instead of blocking under backpressure while the loop is still
/// running, and any drop there would be invisible and unrecoverable — the
/// client never sees a `seq` on the wire (`seq` is gateway-local,
/// ADR-014), so section 7.3 derives its own count from `headSeq` on
/// `attached` plus one per event *yielded*, not per event received (see
/// `close()`'s doc comment for why that distinction is load-bearing).
/// Unbounded growth under a parked consumer is the accepted risk in trade
/// for that guarantee.
///
/// This is a guarantee about the *running* loop only. `close()` does not
/// extend it to a frame already sitting in the transport, unconsumed, at
/// the moment of a local close — see `close()`'s doc comment.
///
/// This actor never spans more than one `Task` at a time (`readLoopTask`,
/// started by `connect()` and torn down by `close()`) — see
/// `home/TOOLCHAIN-NOTES.md` for a suspected Swift 6.3.3 defect this tree
/// works around elsewhere by keeping such fields singular and named rather
/// than collected.
///
/// `close()` ends the stream itself rather than delegating that to the
/// read loop noticing the close — see `close()`'s own doc comment for why:
/// in short, `WebSocketGatewayTransport.close()` cannot be relied on to
/// wake an in-flight `receive()`, and the actor, not a loop that may never
/// run again, is what owes the consumer a terminated stream.
public actor GatewayConnection {
    private let transport: any GatewayTransport
    private var readLoopTask: Task<Void, Never>?
    private var continuation: AsyncThrowingStream<GatewayInboundItem, Error>.Continuation?

    public init(transport: any GatewayTransport) {
        self.transport = transport
    }

    /// Connects the transport and starts the read loop, returning the
    /// stream of inbound items.
    ///
    /// The stream finishes normally once `close()` unwinds the loop via
    /// the transport reporting `GatewayTransportError.closedLocally`; it
    /// finishes by throwing on any other error the transport's `receive()`
    /// raises — in particular `.closed(code:reason:)`, carrying the host's
    /// close code and reason verbatim, so a caller can tell "this
    /// connection was superseded by a newer attach" (4409) apart from
    /// "the core failed" (4500) apart from "the session no longer exists"
    /// (4404) rather than seeing one generic disconnection.
    public func connect() async throws -> AsyncThrowingStream<GatewayInboundItem, Error> {
        try await transport.connect()

        let (stream, continuation) = AsyncThrowingStream<GatewayInboundItem, Error>.makeStream(
            bufferingPolicy: .unbounded
        )
        self.continuation = continuation

        readLoopTask = Task { [weak self] in
            await self?.runReadLoop()
        }

        return stream
    }

    /// Sends one outbound control frame — `create` and `attach` in
    /// section 7's handshake go through here, not through the transport
    /// directly.
    public func send(_ frame: GatewayControlFrame) async throws {
        try await transport.send(ControlFrameCodec.encode(frame))
    }

    /// Closes the transport, cancels the read loop, and finishes the
    /// stream itself — deliberately not by waiting for the loop to notice
    /// and finish it. A previous version of this method awaited the
    /// loop's `Task` (first unbounded, then bounded by a timeout); both
    /// were wrong for the same reason a real `WebSocketGatewayTransport`
    /// can prove: cancelling a `Task` does not make an in-flight
    /// `receive()` return, `URLSessionWebSocketTask.cancel(with:reason:)`
    /// is documented to return without waiting for it either, and a
    /// `withTaskGroup`-based timeout cannot make its scope exit while an
    /// uncooperative child keeps running — so no amount of waiting here,
    /// bounded or not, was ever going to be a correctness fix.
    ///
    /// So this makes no attempt to wait. `close()` returns deterministically,
    /// with no timing assumption anywhere in it, and the stream is
    /// terminated as part of that same call — never left for a loop that
    /// might not still be able to run one. The residual this
    /// leaves: on a live socket whose `receive()` never unblocks, the read
    /// loop's `Task` can outlive this call, orphaned, still running against
    /// an already-closed transport until it is eventually deallocated. That
    /// is real, is not fixable from this side of the transport abstraction,
    /// and is not silently swept under "close() succeeded" by this comment.
    ///
    /// **A second, distinct residual: a frame the transport already has
    /// buffered, but the loop has not yet dequeued, is lost.** `close()`
    /// finishes the stream immediately rather than giving the loop a
    /// chance to drain what is already sitting there — there is no
    /// non-suspending "take what you already have" operation on
    /// `GatewayTransport`, and adding one only to serve this corner case
    /// would still not be implementable meaningfully by
    /// `WebSocketGatewayTransport`, so it would be a fix that works only
    /// against `InMemoryGatewayTransport`
    /// (`aLocalCloseDoesNotDeliverAFrameStillBufferedInTheTransport` in
    /// `GatewayConnectionTests` pins the drop directly). This is
    /// deliberately accepted, not overlooked, on one condition this type
    /// does not itself enforce: **section 7.3's sequence counter must
    /// increment when an event is *yielded*, never when one is merely
    /// received.** Under that rule a frame lost here is exactly
    /// equivalent to the connection having died the instant before it
    /// arrived — never counted, so the client's `lastSeq` stays honest and
    /// a later reattach replays it, the same recovery section 7 already
    /// relies on for an ordinary dropped connection. That recovery
    /// requires a reattach to actually happen: on a final close — the app
    /// quitting, with no reattach coming — a frame lost this way is gone,
    /// the same as any other event mid-turn when a session is not
    /// resumed. It is not lost in the course of ordinary reconnect-and-
    /// resume use, which is the case this trade-off is for.
    public func close() async {
        await transport.close()
        readLoopTask?.cancel()
        readLoopTask = nil
        finish()
    }

    private func runReadLoop() async {
        while !Task.isCancelled {
            let raw: String
            do {
                raw = try await transport.receive()
            } catch GatewayTransportError.closedLocally {
                // A local `close()` is a normal exit, not a failure to
                // report — the stream finishes without an error so a
                // caller does not have to pattern-match its own request
                // to disconnect back out of an error case. In the common
                // case `close()` has already finished the stream itself by
                // the time this runs; `finish()` is then a harmless no-op
                // (see its own doc comment).
                finish()
                return
            } catch {
                finish(throwing: error)
                return
            }

            // No `Task.isCancelled` check between having `raw` in hand and
            // routing it: `InMemoryGatewayTransport.receive()` (and a real
            // socket) can return an already-queued frame without itself
            // observing a concurrent close, so a check here would discard
            // a frame the peer already sent — the exact silent-desync
            // failure this type exists to prevent. Once a frame is in
            // hand, it is always routed; only the checkpoint before
            // `receive()` — where nothing can be lost — skips a further
            // read.
            await route(raw)
        }
        finish()
    }

    /// Finishes `continuation` (if one is still stored) and clears it.
    /// Safe to call more than once, from `close()` and/or from this loop:
    /// `continuation` is nilled out the first time this runs, so a second
    /// call is a no-op via the `nil` check before `AsyncThrowingStream`'s
    /// own finished-continuation behaviour is ever reached. That
    /// underlying behaviour is itself safe regardless —
    /// `yieldingIntoAnAlreadyFinishedContinuationIsANoOpNotACrash` in
    /// `GatewayConnectionTests` pins it directly — which is what makes it
    /// safe for a wedged read loop to wake later (if it ever does) and
    /// call `route()` against a `continuation` this method already
    /// finished: nothing here assumes that yield's target is still
    /// listening.
    private func finish(throwing error: Error? = nil) {
        if let error {
            continuation?.finish(throwing: error)
        } else {
            continuation?.finish()
        }
        continuation = nil
    }

    /// Decodes one raw frame and dispatches it. A decode failure is
    /// logged and the loop continues — one malformed frame from the peer
    /// must never end the connection, mirroring
    /// `ControlFrameSerializer.GetGwDiscriminator`'s type-tolerance on the
    /// C# side of this same untrusted boundary.
    private func route(_ raw: String) async {
        let frame: GatewayFrame
        do {
            frame = try ControlFrameCodec.decode(raw)
        } catch {
            logger.error("dropping frame that failed to decode: \(String(describing: error), privacy: .public)")
            return
        }

        switch frame {
        case .control(.ping):
            await replyToPing()
        case .control(let controlFrame):
            continuation?.yield(.control(controlFrame))
        case .event(let raw):
            continuation?.yield(.event(raw))
        case .unrecognizedControl(let gw, _):
            logger.notice("skipping unrecognized control frame: gw=\(gw, privacy: .public)")
        }
    }

    /// Sent directly against the transport rather than via `continuation`
    /// — see the type-level doc comment for why this must never depend on
    /// the consumer. A failure sending the pong is logged, not fatal: the
    /// next `receive()` in `runReadLoop()` will surface the same
    /// underlying transport failure through the stream if the connection
    /// is really gone.
    private func replyToPing() async {
        do {
            try await transport.send(ControlFrameCodec.encode(.pong))
        } catch {
            logger.error("failed to send pong: \(String(describing: error), privacy: .public)")
        }
    }
}
