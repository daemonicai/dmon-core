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

    func connect() async throws {
        hasConnected = true
    }

    func send(_ frame: String) async throws {
        try checkOperable()
        sent.append(frame)
    }

    func receive() async throws -> String {
        guard hasConnected else { throw GatewayTransportError.notConnected }
        while inbox.isEmpty {
            if closedLocally { throw GatewayTransportError.closedLocally }
            if let peerCloseError { throw peerCloseError }
            await suspendUntilActivity()
        }
        return inbox.removeFirst()
    }

    /// A no-op when never connected (`GatewayTransportError.notConnected`
    /// stays the reported state), otherwise marks the transport
    /// `.closedLocally`. Safe to call more than once.
    func close() async {
        guard hasConnected else { return }
        closedLocally = true
        wakeWaiters()
    }

    /// Makes `frame` available to the next `receive()` call (or a
    /// `receive()` already suspended waiting for one).
    func enqueue(_ frame: String) {
        inbox.append(frame)
        wakeWaiters()
    }

    /// Every frame passed to `send(_:)` so far, in order.
    func sentFrames() -> [String] {
        sent
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

    private func wakeWaiters() {
        let pending = waiters
        waiters.removeAll()
        for waiter in pending {
            waiter.resume()
        }
    }
}
