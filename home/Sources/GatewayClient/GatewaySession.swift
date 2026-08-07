import Foundation

/// Why `GatewaySession.createSession(agent:)` or `.attach(sessionId:lastSeq:)`
/// failed for a reason specific to the create→attach handshake, as opposed
/// to a transport-level failure (`GatewayTransportError`) or a wire-protocol
/// refusal (`WireVersion.CompatibilityError`) — neither of which this type
/// wraps; both propagate through `createSession`/`attach` unchanged (see
/// their doc comments for why).
///
/// Never carries a secret or a raw frame body: `.createRejected` carries
/// only the `code`/`message` pair the gateway itself already intends for
/// direct display (`CreateRejectedFrame`'s doc comment), and the two
/// "connection ended" cases carry nothing beyond which phase was in
/// progress.
public enum GatewaySessionError: Error, Hashable, Sendable {
    /// The gateway replied `createRejected` to a `create`. Carries `code`
    /// and `message` verbatim from `CreateRejectedFrame`, for direct
    /// display — `code` is one of `unknown_agent`, `cap_reached`,
    /// `core_timeout` today, but is not modelled as a closed set here
    /// either, for the same reason `CreateRejectedFrame.code` is not: a
    /// caller that cannot represent a code the gateway adds later is worse
    /// than one that merely displays it.
    case createRejected(code: String, message: String)

    /// The create-only connection's stream ended — normally, or because
    /// the peer closed it (in which case the underlying
    /// `GatewayTransportError.closed(code:reason:)` is thrown directly
    /// instead of this case; see `createSession(agent:)`'s doc comment) —
    /// before a `created` or `createRejected` reply ever arrived.
    case connectionClosedBeforeCreated

    /// The attach connection's stream ended — normally, or because the
    /// peer closed it (same caveat as above) — before an `attached` reply
    /// ever arrived.
    case connectionClosedBeforeAttached

    /// A second call to `attach(sessionId:lastSeq:)` arrived on this
    /// `GatewaySession` while a first call was still between its own start
    /// and completion. Refused outright rather than let it proceed and
    /// overwrite the first call's in-progress state.
    case attachAlreadyInFlight

    /// `attach(sessionId:lastSeq:)` was called on a `GatewaySession` that
    /// already has a live, previously-completed attach and has not been
    /// `close()`d since. The caller wants to resume an existing session,
    /// not start a fresh one from a plain `attach` — that is a reattach's
    /// job (not part of this block; see `performAttach`'s doc comment).
    case alreadyAttached
}

/// Establishes and holds one gateway session: the create→attach handshake
/// (ADR-003/ADR-012, this module's spec `dmon-home-gateway-client`), the
/// session-scoped stream of inbound items that follows it, and the
/// `generation`/`headSeq` recorded from the `attached` reply.
///
/// `create` and `attach` cannot share a connection — the network host
/// (`Dmon.Network`'s `NetworkConnectionEndpoint`) reads exactly one frame
/// per connection before either handling it and returning (`create`,
/// disposing the socket on return) or switching into its forwarding loop
/// (`attach`). So each call below that needs one opens a fresh connection
/// via `makeTransport`, wrapped in its own `GatewayConnection`.
///
/// `attach`'s connection is kept alive for the life of the returned stream:
/// a pump `Task` (`pumpTask`) consumes that `GatewayConnection`'s own
/// stream and re-yields each item into this session's own continuation
/// (`outputContinuation`) via the actor-isolated `routeFromPump(_:)` — the
/// same "single named `Task` field plus a routing method it calls per item"
/// shape `GatewayConnection` itself already uses for `readLoopTask` /
/// `route(_:)`. The returned stream belongs to the session, not to
/// whichever connection currently feeds it — a necessary property for a
/// future reattach to swap the connection underneath a live stream without
/// a consumer ever re-subscribing, but not by itself a sufficient one: see
/// `performAttach(sessionId:lastSeq:)`'s doc comment for the
/// pump-generation gap a reattach will still have to close.
///
/// `attach(sessionId:lastSeq:)` refuses a second, concurrent call
/// (`.attachAlreadyInFlight`) and a call made while this session already
/// has a live, completed attach (`.alreadyAttached`) rather than letting
/// either overwrite the state a first call is still establishing or
/// already established — see that method's own doc comment for how each
/// is detected.
///
/// This type deliberately builds no client-side handshake timeout. The
/// host bounds `create` itself (`CreateHandshakeTimeoutSeconds`, default
/// 30s) and answers `core_timeout` on expiry; a peer that closes the
/// socket surfaces through the transport as `.closed(code:reason:)`
/// regardless of when that happens. The one case this leaves uncovered — a
/// host that holds the connection open and replies nothing at all — has no
/// policy value specified anywhere in this change to bound it by, and the
/// one timeout helper already in this tree
/// (`Supervisor/TimeoutRace.swift`) is both in the wrong module and has a
/// proven defect (`tech-debt/timeout-race-cannot-bound-uncooperative-work.md`),
/// so it is not reached for here. That residual is accepted, not
/// overlooked.
public actor GatewaySession {
    private let makeTransport: @Sendable () -> any GatewayTransport

    private var connection: GatewayConnection?
    private var pumpTask: Task<Void, Never>?
    private var outputContinuation: AsyncThrowingStream<GatewayInboundItem, Error>.Continuation?

    /// Resumed exactly once by `routeFromPump(_:)` on the `attached` frame
    /// that answers the outstanding `attach`, or by `finishPump(throwing:)`
    /// if the pump ends — normally or by throwing — before that frame ever
    /// arrives. `nil`-ed at first use in both places, mirroring
    /// `GatewayConnection.finish(throwing:)`'s "clear the stored slot
    /// before it can be reached a second time" precedent: within one
    /// handshake, this is what keeps a stray later item, or a pump racing
    /// its own completion, from resuming the same waiter twice.
    ///
    /// This field alone does **not** protect against a *second, concurrent*
    /// call to `attach(sessionId:lastSeq:)` overwriting it before the
    /// first call's waiter is ever resumed — nothing about nilling a slot
    /// at first use stops a second write from replacing it first. That
    /// case is ruled out one level up, by `attach`'s own `attachInFlight`
    /// guard, which never lets a second call reach the point where it
    /// would write here at all.
    private var attachWaiter: CheckedContinuation<Void, Error>?

    /// Set synchronously at the top of `attach(sessionId:lastSeq:)`,
    /// before that method's first `await`, and cleared in its `defer` when
    /// the call ends (success or failure). See `attach`'s doc comment for
    /// why that placement makes the check-and-set atomic against a second
    /// concurrent call.
    private var attachInFlight = false

    /// `true` from the moment a call to `attach(sessionId:lastSeq:)`
    /// completes successfully until `close()` next runs. Distinct from
    /// `connection`/`outputContinuation` being non-`nil`, which becomes
    /// true *during* `performAttach(sessionId:lastSeq:)`, before the
    /// handshake itself has completed — using either of those for this
    /// check would misreport an in-flight attach as an already-completed
    /// one.
    private var isAttached = false

    public private(set) var sessionId: String?
    public private(set) var generation: Int64?
    public private(set) var headSeq: Int64?

    public init(makeTransport: @escaping @Sendable () -> any GatewayTransport) {
        self.makeTransport = makeTransport
    }

    /// Opens a throwaway connection, sends `create`, and waits for the
    /// reply. Closes that connection itself before returning or throwing —
    /// nothing about this session's state (`sessionId`, `generation`,
    /// `headSeq`) is touched here; those are `attach`'s job.
    ///
    /// Any item other than the `created`/`createRejected` control frame
    /// that answers `create` — an ADR-003 event, or another control frame
    /// — is skipped rather than ending the wait or being mistaken for a
    /// rejection. The real host never sends such a frame on this
    /// connection (`HandleCreateAsync` sends exactly one reply and
    /// returns), so this is a robustness net, not a documented server
    /// behaviour.
    ///
    /// A peer close reaches the caller as `GatewayTransportError
    /// .closed(code:reason:)`, thrown unchanged rather than wrapped — that
    /// error already carries the `GatewayCloseCode` (in particular 4500,
    /// "session create failed", a real path distinct from the three named
    /// rejection codes) and a message; wrapping it into
    /// `GatewaySessionError` would discard the only actionable material a
    /// caller has for that case. A stream that ends without either a reply
    /// or a thrown error throws `GatewaySessionError
    /// .connectionClosedBeforeCreated`.
    public func createSession(agent: String? = nil) async throws -> String {
        let connection = GatewayConnection(transport: makeTransport())
        let stream = try await connection.connect()

        do {
            try await connection.send(.create(CreateFrame(agent: agent)))
        } catch {
            await connection.close()
            throw error
        }

        var iterator = stream.makeAsyncIterator()
        while true {
            let item: GatewayInboundItem?
            do {
                item = try await iterator.next()
            } catch {
                await connection.close()
                throw error
            }

            guard let item else {
                await connection.close()
                throw GatewaySessionError.connectionClosedBeforeCreated
            }

            guard case .control(let control) = item else {
                continue
            }

            switch control {
            case .created(let created):
                await connection.close()
                return created.sessionId
            case .createRejected(let rejected):
                await connection.close()
                throw GatewaySessionError.createRejected(code: rejected.code, message: rejected.message)
            case .attach, .attached, .ack, .create, .ping, .pong:
                continue
            }
        }
    }

    /// Establishes a new attach handshake on a fresh connection — see
    /// `performAttach(sessionId:lastSeq:)` for what that handshake actually
    /// does. This method itself only decides whether it is safe to start
    /// one at all, refusing outright, before opening anything, in the two
    /// situations a plain `attach` cannot honour:
    ///
    /// - **`.attachAlreadyInFlight`**: another call to this method is
    ///   already between here and its own completion. `attachInFlight` is
    ///   checked and set synchronously, before this method's first
    ///   `await` — an actor-isolated method body runs without
    ///   interruption up to its first suspension point, so no second call
    ///   can observe `attachInFlight` as `false` while a first call is
    ///   still inside that same synchronous prelude, regardless of how the
    ///   two calls happen to be scheduled relative to each other.
    /// - **`.alreadyAttached`**: a prior call to this method already
    ///   completed successfully and this session has not been `close()`d
    ///   since. The right call for that case is a reattach, not another
    ///   `attach` — this method does not silently replace a live
    ///   session's state to accommodate it.
    public func attach(sessionId: String, lastSeq: Int64) async throws -> AsyncThrowingStream<GatewayInboundItem, Error> {
        guard !attachInFlight else {
            throw GatewaySessionError.attachAlreadyInFlight
        }
        guard !isAttached else {
            throw GatewaySessionError.alreadyAttached
        }
        attachInFlight = true
        defer { attachInFlight = false }

        return try await performAttach(sessionId: sessionId, lastSeq: lastSeq)
    }

    /// The connect → send `attach` → await `attached` sequence itself:
    /// opens a fresh connection, sends `attach` with `sessionId` and
    /// `lastSeq`, and waits for the reply that answers it — recording
    /// `generation` and `headSeq` from that frame, and marking this
    /// session `isAttached`, before returning the session-scoped stream
    /// that `pumpTask` feeds for as long as the connection stays up.
    ///
    /// Called only from `attach(sessionId:lastSeq:)` today, which has
    /// already checked `attachInFlight`/`isAttached` before ever reaching
    /// here — this method enforces neither guard itself. That is
    /// deliberate: it is what will let a future `reattach()` (B3) drive
    /// this exact sequence too, without duplicating it, for a case this
    /// method's own guards would otherwise wrongly refuse — a reattach is
    /// only ever called *because* a session is already attached, so it
    /// must be allowed to call this while `isAttached` is already `true`.
    /// Calling this and tearing down the prior connection are necessary
    /// for a reattach, but **not sufficient** — they are not the whole
    /// story, and nothing below should be read as a claim that they are:
    /// `runPump(stream:)`, `routeFromPump(_:)` and `finishPump(throwing:)`
    /// carry no notion of which pump generation is calling them. Each
    /// acts on `self.outputContinuation`/`self.attachWaiter` as they are
    /// at the moment it runs, not as they were when the pump it belongs to
    /// started. So tearing down the prior connection ends *that*
    /// connection's stream, which ends the *prior* pump's loop normally,
    /// which calls `finishPump(throwing: nil)` — and if a reattach has by
    /// then already replaced `self.outputContinuation` with the new
    /// pump's, that stale call finishes the *new*, current stream, not the
    /// old one. A reattach must supply the generation-scoping this method
    /// and its pump do not have — e.g. not starting the new pump, or not
    /// discarding the old `Task`, until the old one has provably stopped
    /// touching shared state — or it will end the very stream it exists
    /// to preserve. That mechanism is not built here, and must not be
    /// half-built here either: no second pump can exist until B3 adds
    /// `reattach()`, so there is nothing yet to test it against.
    ///
    /// `sessionId`, `outputContinuation` and `connection` are all set on
    /// `self` *before* `pumpTask` is created — deliberately, not merely as
    /// an incidental ordering: `routeFromPump(_:)` is the only place that
    /// later reads `outputContinuation`, and it must never observe it
    /// still `nil` for an item that arrives immediately after `attached`.
    /// Because nothing here suspends between those assignments and the
    /// pump's creation, there is no actor-reentrancy window in which the
    /// pump could run before they are visible.
    ///
    /// The same handshake-error handling `createSession(agent:)` documents
    /// applies here unchanged: a peer close surfaces as
    /// `GatewayTransportError.closed(code:reason:)`, propagated exactly as
    /// the connection's stream threw it (this also covers a
    /// `WireVersion.CompatibilityError` from a wire-protocol mismatch on
    /// `attached` — `GatewayConnection.route(_:)` already tears the
    /// connection down and throws that error through the stream before an
    /// incompatible `attached` frame would ever reach `routeFromPump(_:)`,
    /// so it is never wrapped here either); a stream that ends without
    /// either throws `GatewaySessionError.connectionClosedBeforeAttached`.
    private func performAttach(sessionId: String, lastSeq: Int64) async throws -> AsyncThrowingStream<GatewayInboundItem, Error> {
        let connection = GatewayConnection(transport: makeTransport())
        let stream = try await connection.connect()

        do {
            try await connection.send(.attach(AttachFrame(sessionId: sessionId, lastSeq: lastSeq)))
        } catch {
            await connection.close()
            throw error
        }

        let (outputStream, outputContinuation) = AsyncThrowingStream<GatewayInboundItem, Error>.makeStream(
            bufferingPolicy: .unbounded
        )

        self.connection = connection
        self.sessionId = sessionId
        self.outputContinuation = outputContinuation

        do {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                attachWaiter = continuation
                pumpTask = Task { [weak self] in
                    await self?.runPump(stream: stream)
                }
            }
        } catch {
            // `outputContinuation` is not finished here: the only way this
            // `withCheckedThrowingContinuation` throws is via
            // `finishPump(throwing:)` resuming `attachWaiter` with an
            // error, and `finishPump` has already finished
            // `outputContinuation` (with that same error) by the time it
            // does so — see its doc comment.
            await connection.close()
            self.connection = nil
            self.sessionId = nil
            self.outputContinuation = nil
            self.pumpTask = nil
            throw error
        }

        isAttached = true
        return outputStream
    }

    /// `createSession(agent:)` followed by `attach(sessionId:lastSeq:)`
    /// with `lastSeq: 0` — the handshake a first-ever session establishment
    /// always uses. `attach`'s `lastSeq` stays a parameter on the method
    /// above rather than something this session tracks, because tracking a
    /// cursor across a reattach is B3's job, not this block's.
    public func start(agent: String? = nil) async throws -> AsyncThrowingStream<GatewayInboundItem, Error> {
        let sessionId = try await createSession(agent: agent)
        return try await attach(sessionId: sessionId, lastSeq: 0)
    }

    /// Closes the current connection (if any), stops the pump, and ends
    /// the output stream as a normal, unwinding-locally close — mirroring
    /// `GatewayConnection.close()`'s own "a self-initiated close is a
    /// normal exit" precedent. Safe to call when no `attach` has ever
    /// succeeded, and safe to call while an `attach` handshake is still in
    /// flight: closing the underlying connection ends its stream (with no
    /// error, since this is a local close), which ends the pump's loop
    /// normally, which reaches `finishPump(throwing: nil)` and resumes a
    /// still-outstanding `attachWaiter` with
    /// `GatewaySessionError.connectionClosedBeforeAttached` rather than
    /// leaving the caller of `attach` waiting forever.
    ///
    /// Also clears `isAttached`, so a later `attach(sessionId:lastSeq:)`
    /// on this same session is not wrongly refused as `.alreadyAttached`.
    public func close() async {
        await connection?.close()
        connection = nil
        pumpTask?.cancel()
        pumpTask = nil
        isAttached = false
        finishPump(throwing: nil)
    }

    /// Consumes the attach connection's stream for as long as it runs,
    /// handing each item to `routeFromPump(_:)` in order. Ends by calling
    /// `finishPump(throwing:)` exactly once, with the stream's own error
    /// if it threw one or `nil` if it ended normally — never anything
    /// this type invents on its own.
    private func runPump(stream: AsyncThrowingStream<GatewayInboundItem, Error>) async {
        do {
            for try await item in stream {
                routeFromPump(item)
            }
            finishPump(throwing: nil)
        } catch {
            finishPump(throwing: error)
        }
    }

    /// Routes one item from the pump. While `attachWaiter` is still set,
    /// this is the handshake's own wait: the `attached` frame that answers
    /// it records `generation`/`headSeq` and resumes the waiter, and is
    /// not itself forwarded to `outputContinuation` — it is handshake
    /// protocol, not session content. Anything else arriving before
    /// `attached` — an ADR-003 event or another control frame — is
    /// skipped for the same robustness-net reason `createSession(agent:)`
    /// skips one during `create`'s wait; `NetworkConnectionEndpoint`
    /// always answers `attach` with `attached` first.
    ///
    /// Once `attachWaiter` has already been resumed (`nil`), every further
    /// item — including a later `attached` on a connection swapped in by a
    /// reattach a future block adds — is forwarded to
    /// `outputContinuation` unchanged.
    ///
    /// Not `async`: nothing here suspends, so a call to this method runs
    /// to completion without giving the actor up to any other queued call
    /// — there is no reentrancy window inside it to reason about.
    private func routeFromPump(_ item: GatewayInboundItem) {
        if let attachWaiter {
            guard case .control(.attached(let attachedFrame)) = item else {
                return
            }
            generation = attachedFrame.generation
            headSeq = attachedFrame.headSeq
            self.attachWaiter = nil
            attachWaiter.resume()
            return
        }
        outputContinuation?.yield(item)
    }

    /// Ends the handshake wait and the output stream, each at most once.
    /// Both `attachWaiter` and `outputContinuation` are `nil`-ed before
    /// use, the same precedent `GatewayConnection.finish(throwing:)`
    /// documents: a second call — from `close()` racing the pump's own
    /// natural end, in particular — finds both already `nil` and does
    /// nothing.
    ///
    /// A still-outstanding `attachWaiter` is resumed with `error` if the
    /// pump ended by throwing one, or with
    /// `GatewaySessionError.connectionClosedBeforeAttached` if it ended
    /// normally with the handshake never having completed.
    private func finishPump(throwing error: Error?) {
        if let attachWaiter {
            self.attachWaiter = nil
            attachWaiter.resume(throwing: error ?? GatewaySessionError.connectionClosedBeforeAttached)
        }
        if let error {
            outputContinuation?.finish(throwing: error)
        } else {
            outputContinuation?.finish()
        }
        outputContinuation = nil
    }
}
