import Foundation
import Testing
@testable import GatewayClient

/// Polls `condition` until it is true or `timeout` elapses, returning
/// whichever happened. `SupervisorTests` has an equivalent
/// (`waitUntilTrue`, in `TestSupport.swift`), but that lives in a
/// different test target and pulling in a cross-target dependency for one
/// polling loop is not worth it — this suite's few uses stay local.
private func waitUntil(timeout: TimeInterval, condition: @escaping @Sendable () async -> Bool) async -> Bool {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if await condition() { return true }
        try? await Task.sleep(nanoseconds: 5_000_000)
    }
    return await condition()
}

/// Exercises `GatewayConnection`'s read loop — where the transport (B1)
/// and the frame codec (B2) meet — driven entirely through
/// `InMemoryGatewayTransport`, never a live socket.
@Suite
struct GatewayConnectionTests {
    @Test
    func pingIsAnsweredWithPongEvenWhileTheConsumerHasNotDrainedAnything() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        // Nothing ever reads from `stream` before or during this ping —
        // this is the test that would catch the pong queuing behind
        // consumer delivery. A second, distinguishable frame enqueued
        // right after the ping gives a deterministic point to observe
        // from: because `runReadLoop` processes frames strictly in
        // sequence, awaiting `transport.send` for the pong before ever
        // calling `receive()` again, the pong must already be in `sent`
        // by the time this event is read off the transport's read side
        // effect below.
        await transport.enqueue(#"{"gw":"ping"}"#)
        await transport.enqueue(#"{"type":"turn.delta","text":"hi"}"#)

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()

        #expect(item == .event(#"{"type":"turn.delta","text":"hi"}"#))
        #expect(await transport.sentFrames() == [#"{"gw":"pong"}"#])
    }

    /// Regression guard, not a stall reproduction: today `pingIsAnswered
    /// WithPongEvenWhileTheConsumerHasNotDrainedAnything` already proves a
    /// single ping cannot be blocked, because `AsyncThrowingStream.yield`
    /// never suspends under any buffering policy (SE-0314) — so a large
    /// backlog ahead of the ping passes trivially today, for the same
    /// reason. This exists to catch the realistic way that invariant would
    /// actually break in future: `connect()`'s stream being replaced by
    /// something that genuinely applies backpressure (a bounded custom
    /// queue, an `AsyncChannel`). The stream `connect()` returns is never
    /// touched here — `sentFrames()` is polled directly on the transport
    /// so this test does not accidentally supply the draining it is
    /// trying to prove is unnecessary.
    @Test
    func pingIsStillAnsweredAfterABacklogOfUndrainedEvents() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        _ = try await connection.connect()

        for index in 0..<50 {
            await transport.enqueue(#"{"type":"turn.delta","text":"chunk-\#(index)"}"#)
        }
        await transport.enqueue(#"{"gw":"ping"}"#)

        let answered = await waitUntil(timeout: 2) {
            await transport.sentFrames() == [#"{"gw":"pong"}"#]
        }
        #expect(answered)
    }

    @Test
    func anEventIsSurfacedWithItsRawTextUnchanged() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        let raw = #"{"type":"turn.start","turnId":"t1"}"#
        await transport.enqueue(raw)

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()

        #expect(item == .event(raw))
    }

    @Test
    func aRecognisedControlFrameIsSurfacedAsControl() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5}"#)

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()

        #expect(item == .control(.attached(AttachedFrame(generation: 1, headSeq: 5))))
    }

    @Test
    func anUnrecognizedControlFrameIsSkippedNotSurfaced() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"somethingNew","foo":"bar"}"#)
        let raw = #"{"type":"turn.start","turnId":"t1"}"#
        await transport.enqueue(raw)

        // If the unrecognized control frame had leaked into the stream,
        // this would be it instead of the event that follows it.
        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()

        #expect(item == .event(raw))
    }

    @Test
    func aMalformedFrameDoesNotEndTheConnectionAndTheNextGoodFrameStillArrives() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue("{not valid json")
        await transport.enqueue(#"{"gw":"ack","id":"cmd-1"}"#)

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()

        #expect(item == .control(.ack(AckFrame(id: "cmd-1"))))
    }

    @Test
    func aSimulatedPeerCloseSurfacesItsCodeAndReasonToTheConsumer() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.simulateClose(code: .supersededByNewerAttach, reason: "superseded")

        var iterator = stream.makeAsyncIterator()
        await #expect(throws: GatewayTransportError.closed(
            code: .supersededByNewerAttach,
            reason: "superseded"
        )) {
            _ = try await iterator.next()
        }
    }

    @Test
    func aLocalCloseSurfacesAsANormalExit() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await connection.close()

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()

        #expect(item == nil)
    }

    /// `aLocalCloseSurfacesAsANormalExit` above closes immediately after
    /// `connect()`, which cannot guarantee the read loop's own `Task` has
    /// even started running by the time `close()` runs — if it has not,
    /// that test resolves through `receive()`'s pre-suspension
    /// `closedLocally` check and never actually exercises `wakeWaiters()`
    /// waking a genuinely suspended continuation. This test forces that
    /// interleaving explicitly, polling `isReceiverWaiting()` until the
    /// loop is provably parked in `receive()` before closing.
    @Test
    func aLocalCloseWhileTheReadLoopIsGenuinelyParkedInReceiveStillSurfacesANormalExit() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        let parked = await waitUntil(timeout: 2) {
            await transport.isReceiverWaiting()
        }
        #expect(parked)

        await connection.close()

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()

        #expect(item == nil)
    }

    /// Pins the drop `close()`'s own doc comment names explicitly: a frame
    /// already sitting in the transport's inbox, but not yet dequeued by
    /// the read loop, is **not** delivered when `close()` runs — `close()`
    /// finishes the stream immediately rather than giving the loop a
    /// chance to drain what is already available (there is no
    /// non-suspending "take what you already have" operation on
    /// `GatewayTransport`, and the doc comment explains why one is not
    /// worth adding for this). Forces the loop to be genuinely parked in
    /// `receive()` first, then enqueues and closes back-to-back with no
    /// intervening yield — the interleaving under which the drop was
    /// found to reproduce reliably. A test that documents this beats a
    /// comment asserting it: it fails loudly if a future change adds
    /// draining without also revisiting the "count at yield time, not
    /// receive time" rule the doc comment depends on.
    @Test
    func aLocalCloseDoesNotDeliverAFrameStillBufferedInTheTransport() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        let parked = await waitUntil(timeout: 2) {
            await transport.isReceiverWaiting()
        }
        #expect(parked)

        await transport.enqueue(#"{"type":"turn.delta","text":"about to be lost"}"#)
        await connection.close()

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()

        #expect(item == nil)
    }

    /// Pins the stdlib guarantee `GatewayConnection.finish(throwing:)`'s
    /// doc comment relies on, rather than assuming it: a `yield` after
    /// `finish()` must not crash and must not resurrect the stream with a
    /// value no one asked for. This is what makes it safe for a
    /// permanently wedged read loop (see the `uncooperative` tests below)
    /// to wake later — if it ever does — and call `route()` against a
    /// `continuation` `close()` already finished.
    @Test
    func yieldingIntoAnAlreadyFinishedContinuationIsANoOpNotACrash() async throws {
        let (stream, continuation) = AsyncThrowingStream<Int, Error>.makeStream(bufferingPolicy: .unbounded)
        continuation.finish()

        continuation.yield(1)

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()
        #expect(item == nil)
    }

    /// Comfortably above ordinary scheduling/actor-hop jitter, and
    /// comfortably below the multi-second obstacles (`sendDelay`, the
    /// never-resumed `suspendForever()`) the promptness tests below pit
    /// `close()` against — so a regression that reintroduces any wait on
    /// the read loop fails clearly rather than flakily.
    private static let closePromptnessBound: Duration = .seconds(1)

    @Test
    func closeReturnsPromptlyBeforeConnectHasEverBeenCalled() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)

        let start = ContinuousClock.now
        await connection.close()
        let elapsed = start.duration(to: .now)

        #expect(elapsed < Self.closePromptnessBound)
    }

    @Test
    func closeIsIdempotentAndReturnsPromptlyBothTimes() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        _ = try await connection.connect()
        await connection.close()

        let start = ContinuousClock.now
        await connection.close()
        let elapsed = start.duration(to: .now)

        #expect(elapsed < Self.closePromptnessBound)
    }

    /// The headline property this round of review exists to pin: `close()`
    /// must return in bounded time even when the read loop is parked in a
    /// `receive()` that — like the reported real-world
    /// `URLSessionWebSocketTask` behaviour — never unblocks, not even
    /// after the transport is closed. Proving the stream still finishes
    /// requires `close()` to have finished it itself: this wedged loop's
    /// `Task` never calls `finish()` on its own.
    @Test
    func closeReturnsPromptlyAndStillFinishesTheStreamWhileTheReadLoopIsWedgedInAnUncooperativeReceive() async throws {
        let transport = InMemoryGatewayTransport(uncooperative: true)
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        let parked = await waitUntil(timeout: 2) { await transport.isReceiverWaiting() }
        #expect(parked)

        let start = ContinuousClock.now
        await connection.close()
        let elapsed = start.duration(to: .now)

        #expect(elapsed < Self.closePromptnessBound)

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()
        #expect(item == nil)
    }

    /// Confirms `close()`'s promptness does not depend on the read loop
    /// being parked in `receive()` specifically — it is equally prompt
    /// while the loop is genuinely inside `route()`, mid-way through
    /// `replyToPing()`'s slow `send()` call, which is a plain `await`
    /// `close()` has never coupled itself to.
    @Test
    func closeReturnsPromptlyWhileTheReadLoopIsGenuinelyInsideRouteRatherThanParkedInReceive() async throws {
        let transport = InMemoryGatewayTransport(sendDelay: .seconds(3))
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        let parkedBeforePing = await waitUntil(timeout: 2) { await transport.isReceiverWaiting() }
        #expect(parkedBeforePing)

        await transport.enqueue(#"{"gw":"ping"}"#)

        let dequeued = await waitUntil(timeout: 2) { await !(transport.isReceiverWaiting()) }
        #expect(dequeued)
        // The loop has left `receive()`; give it a moment to have entered
        // `replyToPing()`'s slow `send()` — there is no further hook to
        // observe that precisely.
        try? await Task.sleep(for: .milliseconds(100))

        let start = ContinuousClock.now
        await connection.close()
        let elapsed = start.duration(to: .now)

        #expect(elapsed < Self.closePromptnessBound)

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()
        #expect(item == nil)
    }

    @Test
    func sendEncodesAndForwardsAControlFrameToTheTransport() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        _ = try await connection.connect()

        try await connection.send(.attach(AttachFrame(sessionId: "s1", lastSeq: 0)))

        #expect(await transport.sentFrames() == [#"{"gw":"attach","lastSeq":0,"sessionId":"s1"}"#])
    }
}
