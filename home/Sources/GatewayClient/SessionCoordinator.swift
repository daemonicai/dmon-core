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
    /// itself: it is set synchronously, before `close()`'s only suspension
    /// point, so every actor-isolated caller that could observe it —
    /// including `consume(_:)`, running as its own `Task` — sees it as
    /// `true` for the entire window in which the stream could end as a
    /// side effect of this call.
    private var isClosing = false

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

    private func publish() {
        let current = snapshot()
        for subscriber in subscribers.values {
            subscriber.yield(current)
        }
    }

    /// Runs the create→attach handshake at most once. Legal from `.idle`,
    /// `.connectFailed`, and `.dropped` — every other state is a no-op,
    /// deliberately silent rather than thrown: a duplicate call from an app
    /// target status trigger (§B3) must be harmless, not something a caller
    /// has to guard against itself.
    ///
    /// `connectionState = .connecting` is set, and published, synchronously
    /// before this method's first `await` — so a second, concurrent call
    /// to this same method, once it is scheduled on this actor, always
    /// observes `.connecting` and no-ops, regardless of how the two calls
    /// happen to be interleaved by the scheduler.
    public func connect() async {
        switch connectionState {
        case .connecting, .attached:
            return
        case .idle, .connectFailed, .dropped:
            break
        }

        connectionState = .connecting
        publish()

        do {
            let stream = try await session.start(agent: agent)
            guard let sessionId = await session.sessionId else {
                connectionState = .connectFailed(
                    .other(message: "GatewaySession.start(agent:) returned a stream without recording a session id")
                )
                publish()
                return
            }
            connectionState = .attached(sessionId: sessionId)
            publish()
            startConsuming(stream)
        } catch {
            connectionState = .connectFailed(classifyConnectFailure(error))
            publish()
        }
    }

    /// Submits `message` on the current attach connection. Never checks
    /// attach state itself first — `GatewaySession.submitTurn(_:)`'s
    /// `.notAttached` gate is the only one; its own doc comment explains why
    /// that check lives there rather than being left for a caller further
    /// up, and a second, independent copy of it here would be exactly the
    /// kind of drift that reasoning warns against.
    ///
    /// On success, records the submission; on **any** throw — `.notAttached`
    /// or otherwise — records a refusal with a human-readable reason and
    /// keeps the typed text. Either way, exactly one `publish()` follows.
    public func submit(_ message: String) async {
        do {
            try await session.submitTurn(message)
            transcript.recordSubmittedTurn(message)
        } catch {
            transcript.recordSubmissionRefused(message, reason: refusalReason(for: error))
        }
        publish()
    }

    /// Re-establishes the session after a drop, **or retries after a
    /// previous reattach failed** — legal from both `.dropped` and
    /// `.connectFailed`; every other state is a silent no-op, mirroring
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
    public func reattach() async {
        switch connectionState {
        case .idle, .connecting, .attached:
            return
        case .dropped, .connectFailed:
            break
        }

        connectionState = .connecting
        publish()

        do {
            let stream = try await session.reattach()
            guard let sessionId = await session.sessionId else {
                connectionState = .connectFailed(
                    .other(message: "GatewaySession.reattach() returned a stream without recording a session id")
                )
                publish()
                return
            }
            connectionState = .attached(sessionId: sessionId)
            publish()
            startConsuming(stream)
        } catch {
            connectionState = .connectFailed(classifyConnectFailure(error))
            publish()
        }
    }

    /// Closes the underlying session and publishes `.dropped(.closedLocally)`
    /// — always, regardless of what state this coordinator was in
    /// beforehand, matching `GatewaySession.close()`'s own "safe to call
    /// any time" discipline.
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
    /// See `isClosing`'s own doc comment for how that branch is kept from
    /// publishing `.dropped(.streamEnded)` over the top of this method's
    /// own `.closedLocally`.
    public func close() async {
        isClosing = true
        consumingTask?.cancel()
        consumingTask = nil
        await session.close()
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
