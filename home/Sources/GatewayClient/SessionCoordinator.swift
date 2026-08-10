import Foundation

/// What a `SessionCoordinator` currently believes about its connection to
/// the gateway. Nothing in this type times anything out or retries anything
/// — see `SessionCoordinator`'s own doc comment for why: the Product
/// Owner's connect policy is auto-connect once, manual reconnect, and
/// nothing in this change specifies a retry/backoff/give-up policy for this
/// type to invent one of.
public enum GatewayConnectionState: Hashable, Sendable {
    /// `connect()` has never been called, or has never yet succeeded or
    /// failed.
    case idle
    /// A `create`→`attach` handshake is in flight — either the first one
    /// (`connect()`) or a resumption after a drop (`reattach()`).
    case connecting
    /// The handshake succeeded and the stream-consuming task is running.
    case attached(sessionId: String)
    /// A connection that was `.attached` has ended — locally, by the peer,
    /// or because its stream simply ran out. Both `connect()` and
    /// `reattach()` are legal from here, and they do different things:
    /// `connect()` runs a fresh `create`→`attach` handshake and starts a
    /// **brand-new session**, discarding this one and its replay cursor;
    /// `reattach()` resumes *this* session from `lastObservedSeq`. A caller
    /// choosing between the two after a drop wants `reattach()` whenever
    /// the existing conversation is worth keeping.
    case dropped(DisconnectCause)
    /// `connect()` or `reattach()` ran a handshake and it never reached
    /// `.attached` at all.
    case connectFailed(ConnectFailure)

    /// Whether `SessionCoordinator.connect()` will actually run a handshake from this state,
    /// rather than silently no-op. **The single source of truth for that legality** —
    /// `connect()` guards on this property directly rather than repeating its own switch, so a
    /// caller (a view deciding whether to show a "New Session" control, a test) reads the same
    /// fact `connect()` itself acts on instead of a second, independently-maintained copy of it.
    public var allowsConnect: Bool {
        switch self {
        case .idle, .connectFailed, .dropped:
            true
        case .connecting, .attached:
            false
        }
    }

    /// Whether `SessionCoordinator.reattach()` will actually run a handshake from this state,
    /// rather than silently no-op. Same discipline as `allowsConnect`: `reattach()` guards on
    /// this property directly, so this is the one place either needs to change to keep them
    /// from drifting apart. `.connectFailed` is included deliberately — see `reattach()`'s own
    /// doc comment for why a failed reattach must remain retryable, not just a failed connect.
    public var allowsReattach: Bool {
        switch self {
        case .dropped, .connectFailed:
            true
        case .idle, .connecting, .attached:
            false
        }
    }

    /// Why a connection that was `.attached` stopped being so. Distinct
    /// from `ConnectFailure` because these three describe a connection that
    /// genuinely existed and then ended, not one that never got established
    /// — a caller (8.3) needs to tell "we had it and lost it" apart from
    /// "we never had it".
    public enum DisconnectCause: Hashable, Sendable {
        /// The peer closed the connection — `code`/`reason` preserved
        /// verbatim, never flattened to a message string, so a caller can
        /// tell `.supersededByNewerAttach` (4409, fenced out by a newer
        /// attach elsewhere) apart from `.coreFailure` (4500, the core
        /// itself failed) apart from every other code.
        case closedByPeer(code: GatewayCloseCode, reason: String)
        /// `close()` was called on this coordinator.
        case closedLocally
        /// The attach connection's stream ended without either side
        /// reporting an error — `GatewayTransportError`'s own doc comment
        /// on this being a real, distinguishable end state, not a case
        /// this type folds into a peer or local close.
        case streamEnded
        /// Anything this type does not classify structurally. Carries a
        /// description preferring `CustomStringConvertible` over
        /// `String(describing:)` — see `SessionCoordinator.describe(_:)`.
        case other(message: String)
    }

    /// Why a handshake never reached `.attached` at all.
    public enum ConnectFailure: Hashable, Sendable {
        /// The gateway replied `createRejected` — `code`/`message` verbatim
        /// from `GatewaySessionError.createRejected`.
        case createRejected(code: String, message: String)
        /// `WireVersion.checkCompatibility(advertised:)` refused the host's
        /// advertised wire version — `message` is that error's own
        /// human-readable, actionable text.
        case wireVersionMismatch(message: String)
        /// The peer closed the connection before the handshake completed.
        case closedByPeer(code: GatewayCloseCode, reason: String)
        /// Anything this type does not classify structurally, including a
        /// device-key credential refusal this module cannot name — see
        /// `SessionCoordinator.describe(_:)`.
        case other(message: String)
    }
}

/// One point-in-time view of a `SessionCoordinator`: its connection state
/// and its transcript, published together so a subscriber never observes
/// one that disagrees with the other — see `SessionCoordinator.publish()`.
public struct SessionSnapshot: Hashable, Sendable {
    public let connection: GatewayConnectionState
    public let transcript: TurnTranscript
}

/// Owns one `GatewaySession`, drives its inbound stream into a
/// `TurnTranscript`, and publishes `SessionSnapshot`s — connection state and
/// transcript together, as one value — to every subscriber of `updates()`.
/// This is the whole of tasks 8.1/8.2's package half: an app target
/// consumes the stream this type publishes and mirrors it into an
/// `@Observable` value; it decides nothing about connection lifecycle
/// itself.
///
/// **No timer, no retry, no backoff, no "stalled" state anywhere in this
/// type.** The Product Owner's connect policy is auto-connect once, manual
/// reconnect: `connect()` runs a handshake at most once from a connectable
/// state, and the only way back from `.dropped` is an explicit `reattach()`
/// call from further up. A turn that produces nothing on the wire — a real,
/// documented outcome (`TurnEvent.failed`'s own doc comment, the
/// `turn.abort`-before-`turnStart` race) — leaves `TurnTranscript`'s open
/// turn sitting exactly as it is, honestly rendered as still awaiting or
/// streaming, for exactly the same reason `TurnTranscript` itself never
/// invents a timeout: nothing on the wire ever proves silence is permanent.
///
/// **Publish-after-mutate, always as one snapshot.** Every method below
/// that changes `connectionState` or `transcript` calls `publish()` exactly
/// once immediately after, and `publish()` always builds its `SessionSnapshot`
/// from both fields read together, synchronously, with no `await` between
/// the read and the yield. Because this type is an actor, no other
/// actor-isolated call can interleave between a mutation and its `publish()`
/// call, so a subscriber can never observe a transcript that disagrees with
/// the connection state alongside it in the same snapshot.
public actor SessionCoordinator {
    private let session: GatewaySession
    private let agent: String?

    private var connectionState: GatewayConnectionState = .idle
    private var transcript = TurnTranscript()
    private var consumingTask: Task<Void, Never>?

    /// `true` for the duration of `close()`, from before it cancels
    /// `consumingTask` and calls `session.close()` until after it has
    /// published `.dropped(.closedLocally)` itself. `session.close()`
    /// finishes the consuming stream *synchronously*, before it awaits the
    /// transport's own teardown (`GatewaySession.close()`'s own doc
    /// comment) — so `consume(_:)`'s own "stream ended" branch can observe
    /// that end and run concurrently with `close()` still in progress. This
    /// flag is what keeps that branch from publishing `.dropped(.streamEnded)`
    /// over the top of the `.closedLocally` `close()` is about to publish
    /// itself.
    ///
    /// **Being set before `close()`'s own suspension point is necessary but not
    /// sufficient — `close()` also awaits `consume(_:)`'s own task to actually finish
    /// before this flag is cleared, and that second half is what this actually depends
    /// on.** Actor isolation serializes *execution*, not the order in which two
    /// independent tasks' suspended continuations happen to be rescheduled — there is no
    /// language guarantee that `consume(_:)`'s continuation (resumed the moment
    /// `session.close()` finishes the stream, partway through its own body) reaches this
    /// actor and runs its `guard !isClosing` check *before* `close()`'s own continuation
    /// resumes and reaches the code that clears this flag. Forced by hand: adding an
    /// unrelated actor call immediately after `close()` returns (B3 review round 1,
    /// `connectAfterCloseIsANoOpAndOpensNoConnection`) made the *other* ordering observable
    /// at roughly even odds in isolation — `consume(_:)`'s check running late, after this
    /// flag had already been cleared, and overwriting `.dropped(.closedLocally)` with
    /// `.dropped(.streamEnded)`. `close()` closes that gap not by hoping for a favourable
    /// race but by `await`ing `consume(_:)`'s task directly before clearing this flag — see
    /// `close()`'s own doc comment.
    private var isClosing = false

    /// `true` from the moment `close()` is called, for the rest of this actor's lifetime —
    /// what makes `close()` **terminal**, not merely another state transition. Set
    /// synchronously at the top of `close()`, before that method's first `await`, the same
    /// placement discipline `attachInFlight`/`isClosing` already use elsewhere in this actor
    /// so that every call scheduled on this actor after that point observes it.
    ///
    /// `connect()` and `reattach()` both check this twice: once at entry (catching a call that
    /// starts after `close()` has already run, the ordinary case) and once more immediately
    /// after their own handshake `await` returns (catching a call that was already past its
    /// entry check — genuinely suspended inside `session.start()`/`session.reattach()` — when
    /// `close()` ran concurrently on this actor during that suspension; actor isolation
    /// serializes execution, not suspension, so `close()` can and does run to completion in
    /// that window). The second check is what keeps a handshake that raced past `close()` from
    /// resurrecting a connection `close()` already ended: on that path this coordinator tears
    /// the session back down (`session.close()`) rather than publishing `.attached`.
    ///
    /// **This is a deliberate one-way door, not an oversight to work around.** `close()` is
    /// called from exactly one place today — `AppDelegate.applicationShouldTerminate`, the
    /// app's own shutdown path — and nothing else calls it. A coordinator that could still
    /// `connect()`/`reattach()` after `close()` would need to answer what "closed, but
    /// reconnectable" means for `isClosing`, `pumpGeneration`-equivalent bookkeeping, and every
    /// caller of `close()` that currently relies on it being the end of this session's story;
    /// nothing in this change needs that answered. If a future UI wants a disconnect-then-
    /// reconnect affordance, that is a **distinct verb** to add then (e.g. `disconnect()`,
    /// leaving `connect()`/`reattach()` legal afterwards) — not a reinterpretation of what
    /// `close()` means today.
    private var isClosed = false

    private var subscribers: [Int: AsyncStream<SessionSnapshot>.Continuation] = [:]
    private var nextSubscriberToken = 0

    public init(session: GatewaySession, agent: String? = nil) {
        self.session = session
        self.agent = agent
    }

    /// The current snapshot, without subscribing to future ones.
    public func snapshot() -> SessionSnapshot {
        SessionSnapshot(connection: connectionState, transcript: transcript)
    }

    /// A live feed of snapshots: the current one immediately, then one per
    /// subsequent mutation — mirroring `ChildLogStore.updates()`'s shape.
    public func updates() -> AsyncStream<SessionSnapshot> {
        let token = nextSubscriberToken
        nextSubscriberToken += 1
        return AsyncStream(bufferingPolicy: .unbounded) { continuation in
            subscribers[token] = continuation
            continuation.yield(snapshot())
            continuation.onTermination = { [weak self] _ in
                Task { await self?.removeSubscriber(token) }
            }
        }
    }

    private func removeSubscriber(_ token: Int) {
        subscribers.removeValue(forKey: token)
    }

    /// Exposed for tests only, to prove `onTermination` actually
    /// unregisters a subscriber rather than leaking its continuation
    /// forever — mirrors `ChildLogStore.subscriberCountForTesting`.
    var subscriberCountForTesting: Int {
        subscribers.count
    }

    /// Test-only hook, called (if set) at the one point `connect()`/`reattach()` name in their
    /// own doc comments: immediately after reading `session.sessionId`, immediately before the
    /// `isClosed` recheck that guards `connectionState = .attached(...)`. `nil` in production,
    /// which is what keeps this from adding any timing behaviour to the real path —
    /// `raceWindowHookForTesting?()` short-circuits without suspending when there is nothing to
    /// call. Exists to let a test genuinely park a `connect()`/`reattach()` call in that exact
    /// window (a `CheckedContinuation`-based double, the same technique
    /// `GatewaySessionTests.GatedTransportFactory` uses to force its own reentrancy windows —
    /// see `SessionCoordinatorTests.GatedRaceWindowHook` and its two callers,
    /// `closeWinsAConnectThatRacesPastTheEntryCheckIntoThePostHandshakeWindow` and
    /// `closeWinsAReattachThatRacesPastTheEntryCheckIntoThePostHandshakeWindow`), rather than
    /// reasoning about whether the window is reachable or hand-timing a flaky reproduction.
    /// Set only through `setRaceWindowHookForTesting(_:)` — see that method's doc comment for
    /// why a plain property assignment from outside this actor is not an option.
    private var raceWindowHookForTesting: (@Sendable () async -> Void)?

    /// The only way to set `raceWindowHookForTesting` from outside this actor. A method rather
    /// than direct property assignment: actor isolation permits an external caller to *read* an
    /// actor's stored property through an `await`-prefixed access (as `subscriberCountForTesting`
    /// above already does), but not to *write* one directly — only through an isolated method.
    func setRaceWindowHookForTesting(_ hook: (@Sendable () async -> Void)?) {
        raceWindowHookForTesting = hook
    }

    private func publish() {
        let current = snapshot()
        for subscriber in subscribers.values {
            subscriber.yield(current)
        }
    }

    /// Runs the create→attach handshake at most once. Legal exactly where
    /// `connectionState.allowsConnect` says so (`.idle`, `.connectFailed`,
    /// `.dropped`) — every other state is a no-op, deliberately silent
    /// rather than thrown: a duplicate call from an app target status
    /// trigger (§B3) must be harmless, not something a caller has to guard
    /// against itself. Also a silent no-op once `close()` has
    /// ever been called on this coordinator — see `isClosed`'s own doc
    /// comment for why that is a one-way door, and for the `isClosed`
    /// recheck below that makes it hold against a call already in flight
    /// when `close()` runs.
    ///
    /// `connectionState = .connecting` is set, and published, synchronously
    /// before this method's first `await` — so a second, concurrent call
    /// to this same method, once it is scheduled on this actor, always
    /// observes `.connecting` and no-ops, regardless of how the two calls
    /// happen to be interleaved by the scheduler.
    ///
    /// **Every `await` between the entry guard and `connectionState = .attached(...)`
    /// is a window a concurrent `close()` can run to completion in — actor isolation
    /// serializes execution, not suspension.** Both of this method's own suspensions —
    /// `session.start(agent:)` (the whole handshake) and `session.sessionId` (a genuine
    /// cross-actor read: `GatewaySession.sessionId` is `public private(set)`, not
    /// `nonisolated`) — are downstream of exactly one `isClosed` recheck, placed
    /// immediately after the *later* of the two with nothing that suspends between it
    /// and the mutation it guards. That ordering — read `sessionId` first, recheck
    /// once, right before the mutation — is deliberately preferred over rechecking after
    /// each suspension individually: two checks would still leave the `sessionId` read
    /// itself unguarded (the exact gap review round 2 found in an earlier version of
    /// this method, one suspension past a check that looked sufficient), where this
    /// shape cannot regress that way again — there is structurally only one place left
    /// for a suspension to reopen the window, and it is checked. Proved by forcing the
    /// window open, not by this reasoning alone — see `raceWindowHookForTesting`'s own
    /// doc comment and this module's test suite,
    /// `closeWinsAConnectThatRacesPastTheEntryCheckIntoThePostHandshakeWindow`.
    public func connect() async {
        guard !isClosed else { return }
        guard connectionState.allowsConnect else { return }

        connectionState = .connecting
        publish()

        let stream: AsyncThrowingStream<GatewayInboundItem, Error>
        do {
            stream = try await session.start(agent: agent)
        } catch {
            guard !isClosed else { return }
            connectionState = .connectFailed(classifyConnectFailure(error))
            publish()
            return
        }

        let sessionId = await session.sessionId
        await raceWindowHookForTesting?()

        // `close()` may have run to completion on this actor during either suspension
        // above — see `isClosed`'s own doc comment, and this method's own doc comment
        // for why one check here, with nothing that suspends between it and the
        // mutation below, is enough. A handshake that raced past it must not resurrect
        // a connection `close()` already ended; tear the freshly-established session
        // back down instead of publishing `.attached` over `close()`'s own
        // `.dropped(.closedLocally)`.
        guard !isClosed else {
            await session.close()
            return
        }

        guard let sessionId else {
            connectionState = .connectFailed(
                .other(message: "GatewaySession.start(agent:) returned a stream without recording a session id")
            )
            publish()
            return
        }
        connectionState = .attached(sessionId: sessionId)
        publish()
        startConsuming(stream)
    }

    /// Submits `message` on the current attach connection — refusing outright, before touching
    /// `session` at all, when a turn is already open (`transcript.openTurn != nil`). This is the
    /// enforcement point `TurnTranscript`'s own module doc comment names for its positional
    /// attribution to actually depend on: the core's own `turnInProgress` refusal carries no
    /// correlation id, so this client could never tell which of two outstanding turns a stray
    /// event belonged to even if this method let a second submit through. Refusing here, before
    /// a second `turn.submit` frame is ever written, is what keeps "at most one open turn" true
    /// on this side, regardless of what the core's own gate does.
    ///
    /// **Opens the turn before awaiting the write, not after — deliberately.** An earlier shape
    /// of this method called `recordSubmittedTurn(_:)` only once `session.submitTurn(_:)` had
    /// already returned, which left a real window open: that method's own `await` (the socket
    /// write) can suspend past an inbound event for the very turn being submitted, delivered by
    /// `consume(_:)` — a different, concurrently-scheduled task on this same actor — before this
    /// method's continuation resumes. `TurnTranscript.apply(_:)` has no open turn to fold that
    /// event into yet in that shape, so it synthesises a fresh entry (its own no-open-turn
    /// branch) that `recordSubmittedTurn(_:)` then silently displaces the moment it finally
    /// runs — the entry the event actually belonged to. Calling `recordSubmittedTurn(_:)` first,
    /// synchronously, before the `await` below can ever suspend, closes that window the same way
    /// it closes the concurrent-submit one above: by the time anything else can run on this
    /// actor, the entry this turn's events belong to already exists and is already `openTurn`.
    ///
    /// That means a **write failure** — `.notAttached`, or the write itself throwing — has to
    /// unwind an already-open turn, not merely skip opening one — but by the time that `catch`
    /// runs, `session.submitTurn(_:)`'s own `await` has already given `consume(_:)` a window to
    /// run on this actor too, so the turn `recordSubmittedTurn(_:)` opened may no longer be
    /// `transcript.openTurn` at all (replay draining after a `reattach()` is a realistic source —
    /// see `TurnTranscript.convertOpenTurnToRefusal(_:reason:)`'s own doc comment for exactly how
    /// that happens and why it matters). The id `recordSubmittedTurn(_:)` returns is what lets
    /// that method tell "still the same turn" apart from "something else happened to it while the
    /// write was in flight" — passing the id back here, not discarding it, is what makes this
    /// call correct rather than merely usually correct. Never checks attach state itself first —
    /// `GatewaySession.submitTurn(_:)`'s `.notAttached` gate is the only one; its own doc comment
    /// explains why that check lives there rather than being duplicated here.
    ///
    /// Exactly one `publish()` follows every outcome: the open-turn refusal, a successful submit,
    /// and a submit that opened a turn and then failed to write.
    public func submit(_ message: String) async {
        guard transcript.openTurn == nil else {
            transcript.recordSubmissionRefused(
                message,
                reason: "a turn is still open — wait for it to finish, or abandon it"
            )
            publish()
            return
        }

        let assistantID = transcript.recordSubmittedTurn(message)
        do {
            try await session.submitTurn(message)
        } catch {
            transcript.convertOpenTurnToRefusal(assistantID, reason: refusalReason(for: error))
        }
        publish()
    }

    /// The coordinator half of the "Abandon turn" control (`ContentView`'s wiring): stops this
    /// host's own tracking of the open turn by a person's explicit choice, forwarding straight
    /// to `TurnTranscript.abandonOpenTurn()` — see that method's own doc comment, and
    /// `TranscriptEntry.State.abandoned`'s, for why this is a decision this coordinator only
    /// ever relays, never infers on its own. Touches nothing on `session` or the wire: there is
    /// no `turn.abort` command in this change's scope, so a turn the core is still running keeps
    /// running — this only ever changes what this client displays, and whether `submit(_:)` will
    /// accept another message. A harmless no-op if no turn is open.
    public func abandonOpenTurn() {
        transcript.abandonOpenTurn()
        publish()
    }

    /// Re-establishes the session after a drop, **or retries after a
    /// previous reattach failed** — legal exactly where
    /// `connectionState.allowsReattach` says so (`.dropped` and
    /// `.connectFailed`); every other state is a silent no-op, mirroring
    /// `connect()`'s own discipline. On success, re-subscribes to the
    /// **new** stream `GatewaySession.reattach()` returns: the old one is
    /// already finished and cannot be revived (`GatewaySession`'s own
    /// type-level doc comment).
    ///
    /// `.connectFailed` is accepted here deliberately, not merely
    /// tolerated: `GatewaySession.performAttach`'s own doc calls a
    /// handshake failing mid-reattach *"the ordinary transient case
    /// `reattach()` exists to recover from"* — if this coordinator only
    /// ever accepted a retry from `.dropped`, one failed reattach attempt
    /// would permanently strand a resumable session, with `connect()` (a
    /// brand-new session) the only way left forward. This does not
    /// duplicate `GatewaySession.reattach()`'s own gate, the same
    /// discipline `submit(_:)` follows for `.notAttached`: that method
    /// refuses on its own terms (`.reattachWithoutPriorAttach` if no
    /// attach ever completed even once, `.reattachWhileAttached` if a
    /// connection is still live) regardless of what this guard lets
    /// through — a failed reattach leaves `sessionId` set (assigned only on
    /// success, never rolled back) and `isAttached` false, so a retry from
    /// `.connectFailed` reaches `GatewaySession.reattach()` with
    /// `sessionId != nil` and `!isAttached`, exactly the state that gate
    /// accepts.
    ///
    /// **Same shape as `connect()`, deliberately not diverging** — see that method's own
    /// doc comment for why exactly one `isClosed` recheck, placed after both of this
    /// method's suspensions (`session.reattach()`, then `session.sessionId`) with nothing
    /// that suspends between it and `connectionState = .attached(...)`, is what closes the
    /// window a concurrent `close()` could otherwise win into.
    public func reattach() async {
        guard !isClosed else { return }
        guard connectionState.allowsReattach else { return }

        connectionState = .connecting
        publish()

        let stream: AsyncThrowingStream<GatewayInboundItem, Error>
        do {
            stream = try await session.reattach()
        } catch {
            guard !isClosed else { return }
            connectionState = .connectFailed(classifyConnectFailure(error))
            publish()
            return
        }

        let sessionId = await session.sessionId
        await raceWindowHookForTesting?()

        // See `connect()`'s own comment at the equivalent point — same race, same reason,
        // same fix: `close()` may have run to completion on this actor during either
        // suspension above.
        guard !isClosed else {
            await session.close()
            return
        }

        guard let sessionId else {
            connectionState = .connectFailed(
                .other(message: "GatewaySession.reattach() returned a stream without recording a session id")
            )
            publish()
            return
        }
        connectionState = .attached(sessionId: sessionId)
        publish()
        startConsuming(stream)
    }

    /// Closes the underlying session and publishes `.dropped(.closedLocally)`
    /// — always, regardless of what state this coordinator was in
    /// beforehand, matching `GatewaySession.close()`'s own "safe to call
    /// any time" discipline. **Terminal**: once this method is called,
    /// `connect()` and `reattach()` are permanently no-ops on this
    /// coordinator — see `isClosed`'s own doc comment for why that is a
    /// deliberate one-way door, not an oversight.
    ///
    /// `isClosed = true` is the first thing this method does, synchronously,
    /// before its own first `await` — the same placement discipline
    /// `isClosing` below uses, and for the same reason: every call scheduled
    /// on this actor from this point on, including one already suspended
    /// mid-handshake inside `connect()`/`reattach()`, observes it as `true`
    /// once it next runs on this actor (see those methods' own comments at
    /// their post-handshake `isClosed` check for what "observes it" means
    /// for a call that was already in flight).
    ///
    /// `consumingTask?.cancel()` is defensive, not what actually ends
    /// `consume(_:)`'s loop: a closure-based `AsyncThrowingStream` does not
    /// propagate task cancellation into `for try await`, so cancelling
    /// alone would not stop it. What actually ends the loop is
    /// `session.close()` itself — it finishes the pump's stream
    /// *synchronously*, with no error, before it awaits the transport's own
    /// teardown (`GatewaySession.close()`'s own doc comment) — so
    /// `consume(_:)` takes its **normal end-of-stream branch, not its
    /// `catch` branch**, exactly as if the peer had simply stopped sending.
    ///
    /// **`await priorConsumingTask?.value` is what actually makes `isClosing`'s
    /// guarantee true, not merely likely.** `session.close()` returning only proves the
    /// stream has been *finished*; it says nothing about whether `consume(_:)`'s own
    /// task — an independent `Task`, not something this call is nested inside — has
    /// gotten as far as its own `guard !isClosing` check yet (see `isClosing`'s own doc
    /// comment for the race this closes and how forcing it, not merely reasoning about
    /// it, is what surfaced this gap). Awaiting it here cannot hang: by this point
    /// `session.close()` has already finished the stream `consume(_:)` is reading, so
    /// its `for try await` loop is guaranteed to end promptly, whatever the scheduler's
    /// timing. This is what lets `isClosing` be cleared, and `.dropped(.closedLocally)`
    /// published, only *after* `consume(_:)` has already made its own decision — never
    /// concurrently with it.
    public func close() async {
        isClosed = true
        isClosing = true
        let priorConsumingTask = consumingTask
        priorConsumingTask?.cancel()
        consumingTask = nil
        await session.close()
        await priorConsumingTask?.value
        connectionState = .dropped(.closedLocally)
        isClosing = false
        publish()
    }

    private func startConsuming(_ stream: AsyncThrowingStream<GatewayInboundItem, Error>) {
        consumingTask = Task { [weak self] in
            await self?.consume(stream)
        }
    }

    /// Folds every item of `stream` into `transcript` via
    /// `TurnProjection.project(_:)`, publishing after each event it yields.
    /// An item that projects to `nil` changes nothing and is not published
    /// for. Ends by publishing a `.dropped` cause classified from however
    /// the stream ended — unless `isClosing` is `true`, in which case
    /// `close()` is already publishing `.dropped(.closedLocally)` itself
    /// and this method leaves that alone (see `isClosing`'s own doc
    /// comment for why that check is race-free).
    private func consume(_ stream: AsyncThrowingStream<GatewayInboundItem, Error>) async {
        do {
            for try await item in stream {
                guard let event = TurnProjection.project(item) else {
                    continue
                }
                transcript.apply(event)
                publish()
            }
            guard !isClosing else {
                return
            }
            connectionState = .dropped(.streamEnded)
            publish()
        } catch {
            guard !isClosing else {
                return
            }
            connectionState = .dropped(classifyDisconnectCause(error))
            publish()
        }
    }

    private func classifyConnectFailure(_ error: Error) -> GatewayConnectionState.ConnectFailure {
        if let sessionError = error as? GatewaySessionError, case .createRejected(let code, let message) = sessionError {
            return .createRejected(code: code, message: message)
        }
        if let compatibilityError = error as? WireVersion.CompatibilityError {
            return .wireVersionMismatch(message: compatibilityError.message)
        }
        if let transportError = error as? GatewayTransportError, case .closed(let code, let reason) = transportError {
            return .closedByPeer(code: code, reason: reason)
        }
        return .other(message: describe(error))
    }

    private func classifyDisconnectCause(_ error: Error) -> GatewayConnectionState.DisconnectCause {
        if let transportError = error as? GatewayTransportError {
            switch transportError {
            case .closed(let code, let reason):
                return .closedByPeer(code: code, reason: reason)
            case .closedLocally:
                return .closedLocally
            case .notConnected, .binaryFrameReceived:
                return .other(message: describe(transportError))
            }
        }
        return .other(message: describe(error))
    }

    /// `.notAttached` renders as something an operator can act on; every
    /// other error a submit can throw renders from `describe(_:)`.
    private func refusalReason(for error: Error) -> String {
        if let sessionError = error as? GatewaySessionError, sessionError == .notAttached {
            return "no session is attached"
        }
        return describe(error)
    }

    /// Every `.other(message:)` above routes through here. Prefers a
    /// `CustomStringConvertible` description over `String(describing:)` —
    /// the only way this module can render a `DeviceAuthConnectionRefused`
    /// usefully: that error lives in `DeviceKeys`, which depends on this
    /// module, not the other way around, so it reaches here as an
    /// unrecognised `Error` this method cannot name structurally. Making it
    /// conform to `CustomStringConvertible` (a later block's job) is what
    /// turns this fallback from `String(describing:)`'s type-name-and-fields
    /// dump into an actionable message.
    ///
    /// Casts through `Any` rather than directly from `Error` — casting an
    /// `any Error` existential straight to `any CustomStringConvertible`
    /// triggers a misleading `always succeeds` compiler warning on this
    /// platform (verified in isolation: the direct cast still correctly
    /// fails at runtime for a type that does not conform, e.g.
    /// `GatewaySessionError`, and only the compiler's static analysis of
    /// the existential-to-existential cast is wrong, not its behaviour).
    /// Widening to `Any` first keeps the exact same runtime result while
    /// giving the compiler a shape it does not special-case.
    private func describe(_ error: Error) -> String {
        ((error as Any) as? CustomStringConvertible)?.description ?? String(describing: error)
    }
}
