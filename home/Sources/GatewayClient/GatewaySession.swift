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

    /// `submitTurn(_:)` was called on a `GatewaySession` with no live,
    /// completed attach — either none has ever succeeded, or the
    /// connection it established has since dropped or been `close()`d.
    /// There is no attach connection to send a `turn.submit` command on.
    /// Deliberately placed here, on `GatewaySession`, rather than left for
    /// a caller further up to check `isAttached` itself first — this is
    /// the same attach-state gate `attach`/`reattach` already enforce on
    /// themselves, and a second, independent copy of that check anywhere
    /// else in the app is exactly the kind of drift this type exists to
    /// prevent.
    case notAttached
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
/// (`outputContinuation`) via the actor-isolated `routeFromPump(_:generation:lastSeq:)`
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
    /// `async throws`, not a plain `@Sendable () -> any GatewayTransport` (B5 widened it from
    /// that): a caller that needs to resolve a device-key credential before connecting — read
    /// `devices.json`, read the Keychain, provision, or refuse outright — cannot do any of
    /// that from a synchronous, non-throwing closure. `GatewaySession` itself makes no
    /// decision about what a caller's `makeTransport` does with that room; it only calls it,
    /// on every fresh connection (`createSession`, `performAttach`), never once and cached —
    /// so a closure that re-resolves its credential each time sees a revocation on the very
    /// next connection attempt rather than only after some later, separate check. A
    /// synchronous, non-throwing closure still converts to this type implicitly, so no
    /// existing test double in this suite needed a signature change to keep compiling.
    private let makeTransport: @Sendable () async throws -> any GatewayTransport

    private var connection: GatewayConnection?
    private var pumpTask: Task<Void, Never>?
    private var outputContinuation: AsyncThrowingStream<GatewayInboundItem, Error>.Continuation?

    /// Resumed exactly once by `routeFromPump(_:generation:lastSeq:)` on the
    /// `attached` frame that answers the outstanding `attach`, or by
    /// `finishPump(throwing:generation:)` if the pump ends — normally or by
    /// throwing — before that frame ever
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
    /// installs — that `runPump(stream:generation:lastSeq:)`,
    /// `routeFromPump(_:generation:lastSeq:)` and `finishPump(throwing:generation:)`
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
    /// on the wire (ADR-014: it is gateway-local).
    ///
    /// **Seeded from the `lastSeq` this attach actually sent, clamped to
    /// `attachedFrame.headSeq` — never from `headSeq` alone.** The host does
    /// not send `headSeq` *instead of* a replay: `SessionHandler.Attach`
    /// (`frontends/Dmon.Network/Sessions/SessionHandler.cs:251`) sets its
    /// own delivery cursor to `Math.Clamp(lastSeq, 0, headSeq)` and still
    /// returns the *current* `headSeq` on the `attached` reply
    /// (`NetworkConnectionEndpoint.cs:296-297`), then the drain loop
    /// delivers everything in `(lastSeq, headSeq]` after it. Seeding this
    /// cursor to `headSeq` outright, regardless of what `lastSeq` this
    /// attach sent, double-counts every one of those replayed events on top
    /// of a cursor that already jumped to their upper bound — the next
    /// reattach then sends a `lastSeq` above the host's own `headSeq`, which
    /// `Math.Clamp` silently clips, and the entire gap between the two goes
    /// unreplayed with no error anywhere. Mirroring the host's own clamp
    /// here — `min(max(lastSeq, 0), attachedFrame.headSeq)` — is what keeps
    /// this cursor meaning the same thing on both sides of the wire.
    ///
    /// Then incremented by one in `routeFromPump(_:generation:lastSeq:)` for
    /// every `.event` item yielded — *including* one that races ahead of
    /// the `attached` frame that answers this same attach (see that
    /// method's own doc comment for why that race is ordinary, not a
    /// defect) — and *only* for `.event`: a control frame
    /// (`ack`/`ping`/`pong`/`attached`) never advances it, matching the
    /// host's own rule that only an event consumes a sequence number.
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

    /// Events forwarded to `outputContinuation` while `attachWaiter` was
    /// still set for the pump currently establishing — i.e. before the
    /// `attached` frame that answers this same attach has arrived. Added
    /// onto the seed `routeFromPump(_:generation:lastSeq:)` computes once
    /// `attached` finally does arrive, so an event that raced ahead of it is
    /// counted exactly once: neither dropped (see that method's doc
    /// comment) nor double-counted by a seed that would otherwise assume it
    /// already covers them. Reset to `0` synchronously at the top of every
    /// `performAttach(sessionId:lastSeq:)` call, before that call's pump
    /// starts — a handshake that fails after an event has already raced
    /// ahead of it (the connection drops before `attached` arrives) would
    /// otherwise leave this non-zero for the *next* attempt to inherit.
    private var pendingReplayEventCount: Int64 = 0

    public init(makeTransport: @escaping @Sendable () async throws -> any GatewayTransport) {
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
        let connection = GatewayConnection(transport: try await makeTransport())
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
    ///
    /// A **dropped-but-not-`close()`d** connection is legal to call this on
    /// — only `isAttached` gates it, and a drop (a connection ending on its
    /// own, outside of `close()`/`reattach()`) already clears that inside
    /// `finishPump(throwing:generation:)` without touching `self.connection`
    /// itself. This method therefore tears that stale connection down the
    /// same way `reattach()` tears down the one it supersedes — capturing
    /// it before `performAttach` installs its replacement and closing it
    /// once the new one is settled, success or failure — rather than
    /// overwriting `self.connection` out from under it and leaking its read
    /// loop and transport (`tech-debt/websocket-receive-cancellation-leak.md`
    /// is the shape this avoids repeating). `attach` and `reattach` are
    /// sibling public entry points reachable from the exact same
    /// post-drop state; they must not differ in this teardown discipline
    /// just because one of them (this one) is also the state's first,
    /// no-op case: `staleConnection` is `nil` here on a genuinely first-ever
    /// attach, and `nil?.close()` is a no-op.
    public func attach(sessionId: String, lastSeq: Int64) async throws -> AsyncThrowingStream<GatewayInboundItem, Error> {
        guard !attachInFlight else {
            throw GatewaySessionError.attachAlreadyInFlight
        }
        guard !isAttached else {
            throw GatewaySessionError.alreadyAttached
        }
        attachInFlight = true
        defer { attachInFlight = false }

        let staleConnection = connection

        do {
            let stream = try await performAttach(sessionId: sessionId, lastSeq: lastSeq)
            await staleConnection?.close()
            return stream
        } catch {
            await staleConnection?.close()
            throw error
        }
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
    /// it is closed.** `runPump(stream:generation:lastSeq:)`,
    /// `routeFromPump(_:generation:lastSeq:)` and `finishPump(throwing:generation:)`
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
    /// `outputContinuation` and `connection` are set on `self` *before*
    /// `pumpTask` is created — deliberately, not merely as an incidental
    /// ordering: `routeFromPump(_:generation:lastSeq:)` is the only place
    /// that later reads `outputContinuation`, and it must never observe it
    /// still `nil` for an item that arrives immediately after `attached` —
    /// including one that races ahead of `attached` itself. `generation` is
    /// read from `self.pumpGeneration` immediately beforehand, for the same
    /// reason. Because nothing here suspends between those assignments and
    /// the pump's creation, there is no actor-reentrancy window in which the
    /// pump could run before they are visible, or in which another call
    /// could bump `pumpGeneration` again before this pump captures it.
    ///
    /// `sessionId` is deliberately **not** among them: it is assigned only
    /// once the handshake below has actually succeeded, immediately before
    /// `isAttached = true`. A first-ever `attach(sessionId:lastSeq:)` has
    /// nothing to lose either way (`sessionId` is `nil` until it succeeds
    /// regardless), but a `reattach()` calls into this same method over
    /// state that already exists — and a failed handshake here (the
    /// connection dropping before `attached` arrives, the ordinary
    /// transient case `reattach()` exists to recover from) must leave that
    /// existing `sessionId` alone. Assigning it only on success, rather
    /// than assigning it early and then nilling it back out in the `catch`
    /// below on failure, means there is no window in which a failure could
    /// forget to roll it back: the assignment that would need undoing
    /// simply has not happened yet.
    ///
    /// **B5's earlier suspension window, and what was verified — not merely assumed — about
    /// it.** `try await makeTransport()`, above, is this method's *first* suspension point,
    /// earlier than every one the paragraphs above already cover: it runs before any
    /// `GatewayConnection` exists, so `connection`, `pumpTask`, `outputContinuation`, and
    /// `attachWaiter` are all still `nil` for as long as it takes. Two things were confirmed
    /// by hand about a call landing on this actor during that window, not merely reasoned
    /// about (`GatewaySessionTests`, `aSecondAttachIsRefusedWhileTheFirstIsGenuinelySuspendedInsideMakeTransport`,
    /// `aReattachIsRefusedWhileAnAttachIsGenuinelySuspendedInsideMakeTransport`,
    /// `aCloseDuringASuspendedAttachBumpsGenerationSafelyAndTheAttachStillCompletes` — each
    /// forces a real suspension here via a double whose `makeTransport` genuinely parks on an
    /// uncompleted continuation, not one that merely type-checks as `async throws`):
    ///
    /// - A concurrent `attach`/`reattach` is refused correctly, because `attachInFlight` is
    ///   set synchronously by the caller *before* this method — and therefore before
    ///   `makeTransport()` — is ever reached; nothing about that guard depended on the old,
    ///   later suspension point.
    /// - A concurrent `close()` landing here is a near no-op beyond its own `pumpGeneration`
    ///   bump: with all four fields above still `nil`, `finishPump` finds no `attachWaiter`
    ///   or `outputContinuation` to touch and `await closingConnection?.close()` is `nil`.
    ///   Because `generation` (below) is read *after* this suspension returns, not before it,
    ///   the value this method's own pump captures is already the bumped one — the attach
    ///   this method was in the middle of still completes correctly once released.
    ///
    /// A third fact was checked and is recorded honestly rather than assumed to hold: reading
    /// `generation` from a value captured *before* this suspension instead of after was tried
    /// by hand, and it does make the interleaved-`close()` case above hang — but
    /// `.timeLimit(.minutes(1))` did not end that hang within several minutes of observation
    /// (see that test's own doc comment for the mechanism). So the two bullets above are
    /// confirmed correct for the shipped ordering; they are not proven to fail loudly, only
    /// to fail visibly-if-watched, should this ordering ever regress.
    ///
    /// The same handshake-error handling `createSession(agent:)` documents
    /// applies here unchanged: a peer close surfaces as
    /// `GatewayTransportError.closed(code:reason:)`, propagated exactly as
    /// the connection's stream threw it (this also covers a
    /// `WireVersion.CompatibilityError` from a wire-protocol mismatch on
    /// `attached` — `GatewayConnection.route(_:)` already tears the
    /// connection down and throws that error through the stream before an
    /// incompatible `attached` frame would ever reach
    /// `routeFromPump(_:generation:lastSeq:)`, so it is never wrapped here
    /// either); a stream that ends without either throws
    /// `GatewaySessionError.connectionClosedBeforeAttached`.
    private func performAttach(sessionId: String, lastSeq: Int64) async throws -> AsyncThrowingStream<GatewayInboundItem, Error> {
        let connection = GatewayConnection(transport: try await makeTransport())
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
        self.outputContinuation = outputContinuation
        pendingReplayEventCount = 0
        let generation = pumpGeneration

        do {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                attachWaiter = continuation
                pumpTask = Task { [weak self] in
                    await self?.runPump(stream: stream, generation: generation, lastSeq: lastSeq)
                }
            }
        } catch {
            // `outputContinuation` is not finished here: the only way this
            // `withCheckedThrowingContinuation` throws is via
            // `finishPump(throwing:)` resuming `attachWaiter` with an
            // error, and `finishPump` has already finished
            // `outputContinuation` (with that same error) by the time it
            // does so — see its doc comment.
            //
            // `sessionId` is untouched here — see this method's own doc
            // comment (Blocker 3) for why it is assigned only on success,
            // never assigned-then-rolled-back here.
            await connection.close()
            self.connection = nil
            self.outputContinuation = nil
            self.pumpTask = nil
            throw error
        }

        self.sessionId = sessionId
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

    /// Submits a turn as an ADR-003 `turn.submit` command on the current
    /// attach connection, returning the id it was sent with.
    ///
    /// Refuses with `.notAttached` when this session has no live, completed
    /// attach — checked first, before anything else, the same "refuse
    /// before opening or sending anything" discipline `attach(sessionId:
    /// lastSeq:)`'s own guards follow.
    ///
    /// The id is a fresh `UUID().uuidString` on every call — unique within
    /// this session (the host dedups a `turn.submit` on exact id equality
    /// against an unbounded set for the handler's lifetime, so a fresh id
    /// per call is what keeps this submit from ever being mistaken for a
    /// retry of an earlier one).
    ///
    /// Does not touch `lastObservedSeq`: that cursor counts *received*
    /// ADR-003 events only (`routeFromPump(_:generation:lastSeq:)`'s own doc
    /// comment), and sending a command is neither.
    ///
    /// `connection` is read once, into a local, before the `await` below —
    /// `send`ing on that local rather than re-reading `self.connection`
    /// keeps this call's behaviour tied to the connection that was live
    /// when it started, not whatever `self.connection` happens to hold by
    /// the time the send actually completes.
    @discardableResult
    public func submitTurn(_ message: String) async throws -> String {
        guard isAttached, let connection else {
            throw GatewaySessionError.notAttached
        }
        let id = UUID().uuidString
        let command = TurnSubmitCommand(id: id, message: message)
        let raw = try TurnCommandCodec.encode(command)
        try await connection.sendCommand(raw)
        return id
    }

    /// Consumes the attach connection's stream for as long as it runs,
    /// handing each item to `routeFromPump(_:generation:lastSeq:)` in order. Ends
    /// by calling `finishPump(throwing:generation:)` exactly once, with
    /// the stream's own error if it threw one or `nil` if it ended
    /// normally — never anything this type invents on its own.
    ///
    /// `generation` is fixed for the lifetime of one call to this method —
    /// captured once by `performAttach(sessionId:lastSeq:)` when it starts
    /// the `Task` that runs this — and passed through unchanged to every
    /// item this loop routes and to the `finishPump` call that ends it.
    /// See `performAttach`'s doc comment for what that generation is
    /// checked against and why. `lastSeq` is likewise the exact value this
    /// pump's own `attach`/`reattach` frame carried, fixed for its lifetime —
    /// `routeFromPump(_:generation:lastSeq:)` needs it to seed
    /// `lastObservedSeq` correctly once `attached` arrives.
    private func runPump(stream: AsyncThrowingStream<GatewayInboundItem, Error>, generation: Int64, lastSeq: Int64) async {
        do {
            for try await item in stream {
                routeFromPump(item, generation: generation, lastSeq: lastSeq)
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
    /// For a current-generation item: the `attached` frame that answers the
    /// outstanding handshake records `generation`/`headSeq`/
    /// `lastObservedSeq` and resumes `attachWaiter`, and is not itself
    /// forwarded to `outputContinuation` — it is handshake protocol, not
    /// session content.
    ///
    /// **Everything else — including an item that arrives *before*
    /// `attached` does — is forwarded to `outputContinuation` unchanged,
    /// and, for a `.event` only, counted first.** An item racing ahead of
    /// `attached` is not a malformed edge case to guard against: the host's
    /// own reply path sends `attached` through the connection's *serialized
    /// send funnel*, specifically because `Attach()`
    /// (`frontends/Dmon.Network/Sessions/SessionHandler.cs:296` — its
    /// `_wake.Release()`) has already
    /// released the pump's wake before that reply goes out, so the first
    /// buffered replay event can genuinely reach the wire first
    /// (`NetworkConnectionEndpoint.cs:299-303`). That happens on any attach
    /// with a non-empty replay window — i.e. on an ordinary reattach after a
    /// drop — so silently discarding a pre-`attached` item here (as this
    /// method used to) would lose real session content on the single case
    /// `reattach()` exists to serve. `pendingReplayEventCount` (its own doc
    /// comment) is what lets that early counting be added onto, rather than
    /// overwritten by, the seed the `attached` branch below computes once it
    /// finally arrives.
    ///
    /// See `lastObservedSeq`'s own doc comment for why counting happens at
    /// yield rather than at receive, and for the clamp-to-`headSeq` seed
    /// this method's `attached` branch computes.
    ///
    /// Not `async`: nothing here suspends, so a call to this method runs
    /// to completion without giving the actor up to any other queued call
    /// — there is no reentrancy window inside it to reason about.
    private func routeFromPump(_ item: GatewayInboundItem, generation: Int64, lastSeq: Int64) {
        guard generation == pumpGeneration else {
            return
        }
        if let attachWaiter {
            guard case .control(.attached(let attachedFrame)) = item else {
                // Racing ahead of `attached` — see this method's own doc
                // comment for why that is ordinary, not a defect. Counted
                // into `pendingReplayEventCount`, not `lastObservedSeq`
                // directly: the seed below has not been computed yet, and
                // writing into `lastObservedSeq` here would leave the
                // `attached` branch overwriting it outright rather than
                // adding on top of it.
                if case .event = item {
                    pendingReplayEventCount += 1
                }
                outputContinuation?.yield(item)
                return
            }
            self.generation = attachedFrame.generation
            headSeq = attachedFrame.headSeq
            lastObservedSeq = min(max(lastSeq, 0), attachedFrame.headSeq) + pendingReplayEventCount
            pendingReplayEventCount = 0
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
