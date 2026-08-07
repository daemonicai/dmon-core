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
    /// not start a fresh one from a plain `attach` — that is `reattach()`'s
    /// job.
    case alreadyAttached

    /// `reattach()` was called on a `GatewaySession` that has never
    /// completed a successful `attach(sessionId:lastSeq:)` — there is no
    /// `sessionId`, and no observed sequence cursor, to reattach with.
    case reattachWithoutPriorAttach

    /// `reattach()` was called while this session already has a live,
    /// previously-completed attach that has not since dropped or been
    /// `close()`d. Reattach is for resuming *after* a dropped connection,
    /// never for superseding a connection that is still live — see
    /// `reattach()`'s own doc comment for why that is refused by
    /// construction rather than left to the caller's discipline.
    case reattachWhileAttached
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
/// (`outputContinuation`) via the actor-isolated `routeFromPump(_:generation:)`
/// — the same "single named `Task` field plus a routing method it calls per
/// item" shape `GatewayConnection` itself already uses for `readLoopTask` /
/// `route(_:)`.
///
/// A stream this type returns — from `attach(sessionId:lastSeq:)` or from
/// `reattach()` — never survives past the connection that feeds it: on a
/// drop, `finishPump(throwing:generation:)` finishes it *with* the
/// transport's own error, and a finished `AsyncThrowingStream` cannot be
/// revived. `reattach()` establishes a fresh connection and therefore
/// returns a **new** stream; it does not, and structurally cannot, hand the
/// caller back the one that just ended. See `reattach()`'s own doc comment
/// for why a caller must re-subscribe, and `performAttach(sessionId:lastSeq:)`'s
/// for the pump-generation bookkeeping (`pumpGeneration`) that keeps a
/// superseded pump's late completion from reaching the *new* stream's
/// continuation instead of doing nothing.
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

    /// `true` from the moment a call to `attach(sessionId:lastSeq:)` or
    /// `reattach()` completes successfully until the connection it
    /// established next ends — by `close()`, or by
    /// `finishPump(throwing:generation:)` running for the current
    /// generation because the pump feeding it dropped. Distinct from
    /// `connection`/`outputContinuation` being non-`nil`, which becomes
    /// true *during* `performAttach(sessionId:lastSeq:)`, before the
    /// handshake itself has completed — using either of those for this
    /// check would misreport an in-flight attach as an already-completed
    /// one.
    ///
    /// This is `reattach()`'s own gate (D3, its doc comment): `false` here
    /// is what "the connection has already dropped" means to this type,
    /// since nothing else observes a drop synchronously. `reattach()`
    /// refuses outright while this is still `true` — see its doc comment
    /// for why forcing a live stream closed would desynchronise the cursor
    /// this actor tracks in `lastObservedSeq`.
    private var isAttached = false

    /// Identifies the pump — the `(connection, pumpTask, outputContinuation,
    /// attachWaiter)` quadruple one call to `performAttach(sessionId:lastSeq:)`
    /// installs — that `runPump(stream:generation:)`,
    /// `routeFromPump(_:generation:)` and `finishPump(throwing:generation:)`
    /// are each willing to act for. Bumped exactly twice: at the top of
    /// `close()` and at the top of `reattach()`, in both cases *before* the
    /// connection the previous generation was feeding is torn down or
    /// superseded — see `performAttach`'s doc comment for why a bump that
    /// came any later would not be soon enough. Never bumped by
    /// `attach(sessionId:lastSeq:)` itself: a plain `attach` only ever runs
    /// when no earlier generation's pump could still be pending (its own
    /// guards already refuse a second call while one is in flight or the
    /// session is already attached), so whatever value `close()` last left
    /// here is already correct for it to read unchanged.
    private var pumpGeneration: Int64 = 0

    public private(set) var sessionId: String?
    public private(set) var generation: Int64?
    public private(set) var headSeq: Int64?

    /// The highest event sequence number this session has handed to its
    /// consumer so far, derived rather than received — `seq` never appears
    /// on the wire (ADR-014: it is gateway-local). Seeded from `headSeq` on
    /// `attached`, then incremented by one in `routeFromPump(_:generation:)`
    /// for every `.event` item yielded, and *only* for that case: a control
    /// frame (`ack`/`ping`/`pong`/`attached`) never advances it, matching
    /// the host's own rule that only an event consumes a sequence number.
    ///
    /// Counted at yield, never at receive, per the binding rule
    /// `GatewayConnection.close()`'s doc comment states for the connection
    /// below this one: an event this actor has yielded into
    /// `outputContinuation`'s buffer but its consumer has not yet drained is
    /// still counted here and will not be replayed by a later `reattach()`.
    /// That is safe only because `finish()` does not clear a stream's
    /// already-buffered elements — a consumer draining after a drop still
    /// receives them before the terminal error — and because `reattach()`
    /// refuses to run while a connection is still live (D3), so nothing
    /// ever forces that buffer closed out from under a consumer who has not
    /// finished draining it.
    ///
    /// `nil` until the first `attached` frame this session ever receives;
    /// non-`nil` from then on, including across a drop — `reattach()` reads
    /// it as its own `lastSeq`, falling back to `0` only if it were somehow
    /// called before any attach ever completed (guarded against separately
    /// by `.reattachWithoutPriorAttach`).
    public private(set) var lastObservedSeq: Int64?

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
    /// Called from both `attach(sessionId:lastSeq:)` and `reattach()`,
    /// neither of which this method re-checks: both have already decided,
    /// by the time they call here, that starting a fresh handshake is
    /// correct — `attach` via `attachInFlight`/`isAttached`, `reattach` via
    /// `attachInFlight`/`!isAttached`/`sessionId != nil`. This method's own
    /// job is narrower: run the handshake, and hand the pump it starts a
    /// generation number that makes it safe to run even after its caller
    /// has moved on.
    ///
    /// **The pump-generation gap this method used to leave open, and how
    /// it is closed.** `runPump(stream:generation:)`,
    /// `routeFromPump(_:generation:)` and `finishPump(throwing:generation:)`
    /// each take the generation their own pump was started with, and act
    /// only when it still equals `self.pumpGeneration` — the generation
    /// *currently* live. Without that check, each of them would instead act
    /// on `self.outputContinuation`/`self.attachWaiter` as they happen to
    /// be *at the moment the check would have run*, not as they were when
    /// the pump they belong to started. Concretely: `reattach()` bumps
    /// `pumpGeneration` before it tears down the prior connection or starts
    /// a new one (see its own doc comment) — so by the time this method
    /// reads `pumpGeneration` below, right before creating `pumpTask`, it
    /// is already reading the *new* value, and the pump it starts here
    /// captures that value as its own generation. The *prior* pump's own
    /// stream ending — normally, because `reattach()` closed the
    /// connection feeding it, or abnormally, because that connection
    /// dropped — still runs its `runPump` loop to completion and still
    /// calls `finishPump`, on its *own* Task, on its *own* schedule,
    /// possibly well after this method has already returned the new
    /// stream. Every one of those calls now carries the *old* generation,
    /// which the guard compares against the current one and finds stale —
    /// so it returns immediately, touching neither `outputContinuation`
    /// nor `attachWaiter`. That is what makes generation-scoping, not
    /// timing, the thing this correctness depends on: it holds regardless
    /// of how the prior pump's Task and this method happen to be
    /// scheduled relative to each other.
    ///
    /// `sessionId`, `outputContinuation` and `connection` are all set on
    /// `self` *before* `pumpTask` is created — deliberately, not merely as
    /// an incidental ordering: `routeFromPump(_:generation:)` is the only
    /// place that later reads `outputContinuation`, and it must never
    /// observe it still `nil` for an item that arrives immediately after
    /// `attached`. `generation` is read from `self.pumpGeneration`
    /// immediately beforehand, for the same reason. Because nothing here
    /// suspends between those assignments and the pump's creation, there
    /// is no actor-reentrancy window in which the pump could run before
    /// they are visible, or in which another call could bump
    /// `pumpGeneration` again before this pump captures it.
    ///
    /// The same handshake-error handling `createSession(agent:)` documents
    /// applies here unchanged: a peer close surfaces as
    /// `GatewayTransportError.closed(code:reason:)`, propagated exactly as
    /// the connection's stream threw it (this also covers a
    /// `WireVersion.CompatibilityError` from a wire-protocol mismatch on
    /// `attached` — `GatewayConnection.route(_:)` already tears the
    /// connection down and throws that error through the stream before an
    /// incompatible `attached` frame would ever reach
    /// `routeFromPump(_:generation:)`, so it is never wrapped here either);
    /// a stream that ends without either throws
    /// `GatewaySessionError.connectionClosedBeforeAttached`.
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
        let generation = pumpGeneration

        do {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                attachWaiter = continuation
                pumpTask = Task { [weak self] in
                    await self?.runPump(stream: stream, generation: generation)
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
    /// above rather than something this session tracks for it, because a
    /// first attach has no prior sequence to resume from; `reattach()` is
    /// what reads the cursor `lastObservedSeq` tracks from here on.
    public func start(agent: String? = nil) async throws -> AsyncThrowingStream<GatewayInboundItem, Error> {
        let sessionId = try await createSession(agent: agent)
        return try await attach(sessionId: sessionId, lastSeq: 0)
    }

    /// Re-establishes this session on a fresh connection after a dropped
    /// one, attaching with `lastObservedSeq` as `lastSeq` so the host
    /// replays only what this session has not already observed. Returns a
    /// **new** stream — see the type-level doc comment for why the one a
    /// prior `attach`/`reattach` returned can never be revived, and
    /// re-subscribe to this one instead of assuming the old one keeps
    /// producing items.
    ///
    /// Refused outright, before opening anything, in three situations:
    ///
    /// - **`.attachAlreadyInFlight`**: the same guard `attach` enforces,
    ///   protecting `reattach()` from racing a concurrent `attach()` or
    ///   another `reattach()` exactly as described on `attach`'s own doc
    ///   comment.
    /// - **`.reattachWithoutPriorAttach`**: no `attach(sessionId:lastSeq:)`
    ///   has ever completed on this session, so there is no `sessionId`
    ///   and no `lastObservedSeq` to reattach with.
    /// - **`.reattachWhileAttached`**: the current connection is still
    ///   live (`isAttached`). Reattach is for resuming *after* a drop, not
    ///   for superseding a healthy connection — and the task's own spec
    ///   requirement is only about resuming after a drop, so this method
    ///   does not try to support the other case. Concretely: an event this
    ///   session has already yielded into `outputContinuation`'s buffer,
    ///   but whose consumer has not yet drained, is already counted in
    ///   `lastObservedSeq` (D4, `lastObservedSeq`'s own doc comment). If
    ///   `reattach()` tore that stream down while it was still live, those
    ///   buffered-but-undrained events would be lost for good — counted,
    ///   but never delivered, and never replayed either, since the cursor
    ///   already moved past them. Refusing while `isAttached` is `true`
    ///   rules that out by construction: this method's own buffer is only
    ///   ever discarded once its consumer's chance to drain it is
    ///   genuinely over.
    ///
    /// Generation-scoping (D2, `performAttach`'s doc comment) is what makes
    /// the rest of this method safe: `pumpGeneration` is bumped *before*
    /// the prior connection is torn down, so whatever remains of the prior
    /// pump — including a `finishPump(throwing:generation:)` call that has
    /// not yet run — is already stale by the time this method's own new
    /// pump exists, regardless of which of the two happens to finish
    /// running first.
    public func reattach() async throws -> AsyncThrowingStream<GatewayInboundItem, Error> {
        guard !attachInFlight else {
            throw GatewaySessionError.attachAlreadyInFlight
        }
        guard let sessionId else {
            throw GatewaySessionError.reattachWithoutPriorAttach
        }
        guard !isAttached else {
            throw GatewaySessionError.reattachWhileAttached
        }
        attachInFlight = true
        defer { attachInFlight = false }

        pumpGeneration += 1
        let staleConnection = connection
        let lastSeq = lastObservedSeq ?? 0

        do {
            let stream = try await performAttach(sessionId: sessionId, lastSeq: lastSeq)
            await staleConnection?.close()
            return stream
        } catch {
            await staleConnection?.close()
            throw error
        }
    }

    /// Closes the current connection (if any), stops the pump, and ends
    /// the output stream as a normal, unwinding-locally close — mirroring
    /// `GatewayConnection.close()`'s own "a self-initiated close is a
    /// normal exit" precedent. Safe to call when no `attach` has ever
    /// succeeded, and safe to call while an `attach` handshake is still in
    /// flight: closing the underlying connection ends its stream (with no
    /// error, since this is a local close), which ends the pump's loop
    /// normally, which reaches `finishPump(throwing: nil, generation:)` and
    /// resumes a still-outstanding `attachWaiter` with
    /// `GatewaySessionError.connectionClosedBeforeAttached` rather than
    /// leaving the caller of `attach` waiting forever.
    ///
    /// Bumps `pumpGeneration` before tearing the connection down, the same
    /// generation-scoping discipline `reattach()` follows and for the same
    /// reason: a pump this call is about to supersede must already be
    /// stale by the time anything it belongs to completes, regardless of
    /// scheduling. `finishPump` is called directly, against the
    /// now-current generation, *before* this method awaits the connection's
    /// own teardown — not after — so the stream this call ends, and
    /// `isAttached` becoming `false`, are both visible to a caller the
    /// instant this method's synchronous prelude finishes, rather than only
    /// once the underlying transport has actually finished closing (which
    /// can take a while; nothing here needs to wait for it to keep its own
    /// bookkeeping correct). This is this actor's own instance of the same
    /// fact `GatewayConnection.teardown(throwing:)`'s doc comment states
    /// one layer down — "the stream ended" is not proof "the transport is
    /// closed" — and the reason `await closingConnection?.close()` below
    /// is worth doing at all even though nothing here waits on it.
    ///
    /// Also clears `isAttached` (inside `finishPump`, once it runs), so a
    /// later `attach(sessionId:lastSeq:)` or `reattach()` on this same
    /// session is not wrongly refused as already attached.
    public func close() async {
        pumpGeneration += 1
        let generation = pumpGeneration
        let closingConnection = connection
        connection = nil
        pumpTask?.cancel()
        pumpTask = nil
        finishPump(throwing: nil, generation: generation)
        await closingConnection?.close()
    }

    /// Consumes the attach connection's stream for as long as it runs,
    /// handing each item to `routeFromPump(_:generation:)` in order. Ends
    /// by calling `finishPump(throwing:generation:)` exactly once, with
    /// the stream's own error if it threw one or `nil` if it ended
    /// normally — never anything this type invents on its own.
    ///
    /// `generation` is fixed for the lifetime of one call to this method —
    /// captured once by `performAttach(sessionId:lastSeq:)` when it starts
    /// the `Task` that runs this — and passed through unchanged to every
    /// item this loop routes and to the `finishPump` call that ends it.
    /// See `performAttach`'s doc comment for what that generation is
    /// checked against and why.
    private func runPump(stream: AsyncThrowingStream<GatewayInboundItem, Error>, generation: Int64) async {
        do {
            for try await item in stream {
                routeFromPump(item, generation: generation)
            }
            finishPump(throwing: nil, generation: generation)
        } catch {
            finishPump(throwing: error, generation: generation)
        }
    }

    /// Routes one item from the pump identified by `generation`. Returns
    /// immediately, touching nothing, if `generation` is no longer
    /// `pumpGeneration` — a superseded pump's item, routed after
    /// `reattach()` or `close()` has already moved this session on to a
    /// newer generation. This is the generation-scoping `performAttach`'s
    /// doc comment describes: without it, a stale item could resume the
    /// *current* handshake's `attachWaiter` or yield into the *current*
    /// stream's `outputContinuation`, neither of which belongs to the pump
    /// that received it.
    ///
    /// For a current-generation item: while `attachWaiter` is still set,
    /// this is the handshake's own wait: the `attached` frame that answers
    /// it records `generation`/`headSeq`/`lastObservedSeq` and resumes the
    /// waiter, and is not itself forwarded to `outputContinuation` — it is
    /// handshake protocol, not session content. Anything else arriving
    /// before `attached` — an ADR-003 event or another control frame — is
    /// skipped for the same robustness-net reason `createSession(agent:)`
    /// skips one during `create`'s wait; `NetworkConnectionEndpoint`
    /// always answers `attach` with `attached` first.
    ///
    /// Once `attachWaiter` has already been resumed (`nil`), every further
    /// item is forwarded to `outputContinuation` unchanged, and — for a
    /// `.event` only, never a control frame — advances `lastObservedSeq`
    /// by one first. See `lastObservedSeq`'s own doc comment for why this
    /// increments at yield rather than at receive.
    ///
    /// Not `async`: nothing here suspends, so a call to this method runs
    /// to completion without giving the actor up to any other queued call
    /// — there is no reentrancy window inside it to reason about.
    private func routeFromPump(_ item: GatewayInboundItem, generation: Int64) {
        guard generation == pumpGeneration else {
            return
        }
        if let attachWaiter {
            guard case .control(.attached(let attachedFrame)) = item else {
                return
            }
            self.generation = attachedFrame.generation
            headSeq = attachedFrame.headSeq
            lastObservedSeq = attachedFrame.headSeq
            self.attachWaiter = nil
            attachWaiter.resume()
            return
        }
        if case .event = item {
            lastObservedSeq = (lastObservedSeq ?? 0) + 1
        }
        outputContinuation?.yield(item)
    }

    /// Ends the handshake wait and the output stream belonging to
    /// `generation`, each at most once — and only if `generation` is
    /// still `pumpGeneration`; a stale call (its pump superseded by a
    /// later `reattach()` or `close()` before it got here) returns
    /// immediately, leaving `attachWaiter`/`outputContinuation` — which,
    /// if non-`nil`, belong to a *newer* generation than the one this call
    /// carries — untouched. See `performAttach`'s doc comment for the
    /// scheduling this guards against.
    ///
    /// For a current-generation call: both `attachWaiter` and
    /// `outputContinuation` are `nil`-ed before use, the same precedent
    /// `GatewayConnection.finish(throwing:)` documents: a second call for
    /// the *same* generation — from `close()` racing that generation's own
    /// pump reaching its natural end, in particular — finds both already
    /// `nil` and does nothing further. `isAttached` is cleared
    /// unconditionally here, not only by `close()`'s own body, so that a
    /// drop the pump notices on its own — no `close()`/`reattach()`
    /// involved — also clears it; that is what lets `reattach()`'s guard
    /// (`!isAttached`) recognise a drop has already happened.
    ///
    /// A still-outstanding `attachWaiter` is resumed with `error` if the
    /// pump ended by throwing one, or with
    /// `GatewaySessionError.connectionClosedBeforeAttached` if it ended
    /// normally with the handshake never having completed.
    private func finishPump(throwing error: Error?, generation: Int64) {
        guard generation == pumpGeneration else {
            return
        }
        isAttached = false
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
