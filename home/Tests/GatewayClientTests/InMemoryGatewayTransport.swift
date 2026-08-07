import Foundation
import GatewayClient

/// A `GatewayTransport` conformer that never touches the network: a test
/// enqueues frames for the client to receive with `enqueue(_:)`, observes
/// what the client sent via `sentFrames()`, and simulates a **peer**
/// close with a given code and reason via `simulateClose(code:reason:)`
/// — distinct from a **local** close via `close()`. This is what
/// sections 7 and 8 drive their handshake/turn/event tests through.
///
/// Reports `GatewayTransportError`'s three lifecycle facts exactly as
/// `WebSocketGatewayTransport` does: never connected is `.notConnected`
/// only; a local `close()` is `.closedLocally` only; a simulated peer
/// close is `.closed(code:reason:)` only. `hasConnected` and
/// `closedLocally` are independent flags, so `close()` guards on
/// `hasConnected` at its single write site before setting
/// `closedLocally` — that guard is what keeps a transport that was
/// never connected reporting `.notConnected` rather than
/// `.closedLocally`; removing it would let the two facts contradict
/// each other.
///
/// An `actor`, so a test task and the client task under test can call it
/// concurrently without a race: `receive()` suspends on an empty inbox by
/// awaiting a continuation, which — because awaiting releases the actor's
/// exclusive access — lets a concurrent `enqueue(_:)`, `close()` or
/// `simulateClose` run and wake it.
actor InMemoryGatewayTransport: GatewayTransport {
    private var hasConnected = false
    private var closedLocally = false
    private var peerCloseError: GatewayTransportError?
    private var inbox: [String] = []
    private var sent: [String] = []
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var isWedged = false
    private let isUncooperative: Bool
    private let sendDelay: Duration
    private let closeDelay: Duration
    private var hasDelayedAClose = false
    private var isDelayingAClose = false

    /// - Parameters:
    ///   - uncooperative: When `true`, a `receive()` call that would
    ///     otherwise suspend on an empty inbox suspends **forever**
    ///     instead — not woken by `enqueue(_:)`, `close()`, or
    ///     `simulateClose`. Reproduces, for `GatewayConnectionTests`, the
    ///     reported `URLSessionWebSocketTask` behaviour this module cannot
    ///     itself confirm on a live socket: cancelling the task that owns
    ///     an outstanding async `receive()` does not guarantee it unblocks.
    ///     Default `false` preserves every existing call site's
    ///     cooperative behaviour.
    ///   - sendDelay: An artificial delay `send(_:)` sleeps for before
    ///     recording the frame, letting a test force the interleaving
    ///     "close while the read loop is genuinely inside `route()`,
    ///     rather than parked in `receive()`". Default `.zero`.
    ///   - closeDelay: An artificial delay the **first** call to `close()`
    ///     sleeps for before completing; every later concurrent call
    ///     returns immediately. Lets a test force "two teardown paths
    ///     both reach `transport.close()`, but the second one resumes
    ///     first" — the exact interleaving
    ///     `GatewayConnection.teardown(throwing:)`'s ordering has to get
    ///     right regardless of which caller entered it first. A delay
    ///     applied to every call, rather than only the first, would not
    ///     do this: two concurrent calls sleeping for the same duration
    ///     resume in the same order they started, which never exercises
    ///     the "second entrant resumes first" case this exists to force.
    ///     Default `.zero`.
    init(uncooperative: Bool = false, sendDelay: Duration = .zero, closeDelay: Duration = .zero) {
        self.isUncooperative = uncooperative
        self.sendDelay = sendDelay
        self.closeDelay = closeDelay
    }

    func connect() async throws {
        hasConnected = true
    }

    func send(_ frame: String) async throws {
        try checkOperable()
        if sendDelay > .zero {
            try? await Task.sleep(for: sendDelay)
        }
        sent.append(frame)
    }

    func receive() async throws -> String {
        guard hasConnected else { throw GatewayTransportError.notConnected }
        while inbox.isEmpty {
            if closedLocally { throw GatewayTransportError.closedLocally }
            if let peerCloseError { throw peerCloseError }
            if isUncooperative {
                await suspendForever()
            } else {
                await suspendUntilActivity()
            }
        }
        return inbox.removeFirst()
    }

    /// A no-op when never connected (`GatewayTransportError.notConnected`
    /// stays the reported state), otherwise marks the transport
    /// `.closedLocally`. Safe to call more than once.
    ///
    /// The very first call sleeps for `closeDelay` (if non-zero) before
    /// marking the transport closed; `hasDelayedAClose` guards that so
    /// every subsequent concurrent call — such as a second teardown path
    /// racing the first — returns immediately instead of also sleeping.
    /// See `init(uncooperative:sendDelay:closeDelay:)` for why that
    /// asymmetry is the point.
    func close() async {
        guard hasConnected else { return }
        if closeDelay > .zero, !hasDelayedAClose {
            hasDelayedAClose = true
            isDelayingAClose = true
            try? await Task.sleep(for: closeDelay)
            isDelayingAClose = false
        }
        closedLocally = true
        wakeWaiters()
    }

    /// Makes `frame` available to the next `receive()` call (or a
    /// `receive()` already suspended waiting for one).
    func enqueue(_ frame: String) {
        inbox.append(frame)
        wakeWaiters()
    }

    /// Like `enqueue(_:)`, but appends every frame in `frames` within one
    /// actor-isolated call instead of one `await` per frame. Exists for
    /// `GatewaySessionTests
    /// .aSupersededPumpsBufferedBacklogDoesNotCorruptTheStreamOrCursorAReattachJustInstalled`,
    /// which needs a real backlog to have accumulated in the consuming
    /// stream's buffer *before* anything starts draining it: `enqueue(_:)`
    /// called in a loop gives the read loop a scheduling turn between every
    /// single append, so it drains in near lock-step with the loop instead
    /// of ever falling behind — observed directly while diagnosing that
    /// test, not merely suspected. Batching removes those in-between turns,
    /// so the read loop cannot start pulling any of `frames` until all of
    /// them are already queued.
    func enqueueBatch(_ frames: [String]) {
        inbox.append(contentsOf: frames)
        wakeWaiters()
    }

    /// Every frame passed to `send(_:)` so far, in order.
    func sentFrames() -> [String] {
        sent
    }

    /// Whether a `receive()` call is genuinely suspended right now, waiting
    /// on an empty inbox — either in `suspendUntilActivity()`
    /// (cooperative) or `suspendForever()` (`uncooperative: true`) — as
    /// opposed to merely queued to run. Test-only observability: it lets a
    /// caller force a specific interleaving (act only once a consumer's
    /// read loop is provably parked) rather than approximating it with an
    /// unconditional delay that would either be too short to be reliable
    /// or too long to be fast.
    func isReceiverWaiting() -> Bool {
        !waiters.isEmpty || isWedged
    }

    /// Whether the first, delayed `close()` call is genuinely sleeping in
    /// `closeDelay` right now — as opposed to merely queued to run. Lets a
    /// test wait until that call has provably reached `transport.close()`
    /// before starting a second, concurrent close, the same "act only once
    /// provably parked" precedent `isReceiverWaiting()` sets for
    /// `receive()`.
    func isCloseInFlight() -> Bool {
        isDelayingAClose
    }

    /// Simulates the **peer** closing the connection with `code` and
    /// `reason` — e.g. the network gateway's `4409` or `4500` — distinct
    /// from a **local** `close()`. Makes every subsequent
    /// `receive()`/`send(_:)` call (and any call already suspended in
    /// `receive()`) fail with `GatewayTransportError.closed(code:reason:)`,
    /// once any frames already enqueued have been drained.
    func simulateClose(code: GatewayCloseCode, reason: String) {
        peerCloseError = .closed(code: code, reason: reason)
        wakeWaiters()
    }

    private func checkOperable() throws {
        guard hasConnected else { throw GatewayTransportError.notConnected }
        if closedLocally { throw GatewayTransportError.closedLocally }
        if let peerCloseError { throw peerCloseError }
    }

    private func suspendUntilActivity() async {
        await withCheckedContinuation { continuation in
            waiters.append(continuation)
        }
    }

    /// Suspends and is never resumed, by anything — see
    /// `init(uncooperative:sendDelay:)`. `withUnsafeContinuation`, not
    /// `withCheckedContinuation`: `CheckedContinuation` documents a
    /// leaked-continuation diagnostic (`SWIFT TASK CONTINUATION MISUSE`)
    /// on deinit, escalating to `fatalError` in general use. Chosen
    /// defensively against that documented behaviour, not because it was
    /// observed here — tried with `withCheckedContinuation` against this
    /// exact usage, it printed the misuse warning but did not abort: the
    /// continuation is held by a `Task` that itself never completes, so
    /// its `deinit` never runs in a single test process's lifetime. The
    /// unsafe variant sidesteps the question entirely by performing no
    /// such check, which is also the more honest match for what this mode
    /// models: a `receive()` that is dropped and forgotten, not one that
    /// gets to report its own misuse.
    private func suspendForever() async {
        isWedged = true
        await withUnsafeContinuation { (_: UnsafeContinuation<Void, Never>) in
            // Deliberately never resumed.
        }
    }

    private func wakeWaiters() {
        let pending = waiters
        waiters.removeAll()
        for waiter in pending {
            waiter.resume()
        }
    }
}
