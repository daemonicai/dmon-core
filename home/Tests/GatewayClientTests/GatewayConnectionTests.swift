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

        // `wire` must be present and compatible or `route(_:)` refuses the
        // frame instead of surfacing it — see `GatewayConnectionTests`'
        // wire-version-specific tests below for that behaviour itself.
        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()

        #expect(item == .control(.attached(AttachedFrame(generation: 1, headSeq: 5, wire: "0.2"))))
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
    func aMatchingWireVersionOnAttachedLetsTheFrameProceed() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()

        #expect(item == .control(.attached(AttachedFrame(generation: 1, headSeq: 5, wire: "0.2"))))
    }

    @Test
    func afterAMatchingWireVersionSubsequentFramesStillArrive() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)
        let raw = #"{"type":"turn.start","turnId":"t1"}"#
        await transport.enqueue(raw)

        var iterator = stream.makeAsyncIterator()
        let first = try await iterator.next()
        let second = try await iterator.next()

        #expect(first == .control(.attached(AttachedFrame(generation: 1, headSeq: 5, wire: "0.2"))))
        #expect(second == .event(raw))
    }

    /// The falsification "refuses to proceed" actually needs: a bad
    /// version on `attached` followed immediately by an ADR-003 event
    /// must surface the error and **never** the event. A test that only
    /// checks an error is thrown would still pass if the event slipped
    /// through afterwards.
    ///
    /// This does not assert on a second `iterator.next()` call to prove
    /// that — see `home/TOOLCHAIN-NOTES.md`'s `AsyncThrowingStream` entry
    /// for why a second call cannot be trusted on this toolchain. Instead
    /// this inspects the transport directly: the event enqueued right
    /// behind the bad `attached` frame is still sitting in it, unconsumed,
    /// which is only possible if `runReadLoop()` tore itself down without
    /// ever calling `receive()` again — the read loop, not the stream's
    /// retry count, is what "never delivers" actually depends on.
    @Test
    func aMismatchedWireVersionRefusesTheConnectionAndNeverDeliversTheFrameThatFollows() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"9.9"}"#)
        let eventRaw = #"{"type":"turn.delta","text":"must never arrive"}"#
        await transport.enqueue(eventRaw)

        var iterator = stream.makeAsyncIterator()
        await #expect(throws: WireVersion.CompatibilityError.mismatch(
            client: .current,
            host: WireVersion(major: 9, minor: 9)
        )) {
            _ = try await iterator.next()
        }

        let stillBuffered = try await transport.receive()
        #expect(stillBuffered == eventRaw)

        // Draining that one buffered frame empties the inbox; the
        // transport itself was actually closed, not merely abandoned.
        await #expect(throws: GatewayTransportError.closedLocally) {
            _ = try await transport.receive()
        }
    }

    /// Pins the property `teardown(throwing:)`'s ordering exists for:
    /// `finish(throwing:)` running before the first `await` is what makes
    /// the **first entrant** into `teardown(throwing:)` the one whose
    /// outcome wins, regardless of which caller's `await
    /// transport.close()` happens to resume first.
    ///
    /// The bad-wire `attached` frame drives `route(_:)` into
    /// `teardown(throwing:)` with a `CompatibilityError` — the first
    /// entrant. `InMemoryGatewayTransport(closeDelay:)` makes that call's
    /// `transport.close()` sleep, and `isCloseInFlight()` lets this test
    /// wait until it provably has (not merely been scheduled) before
    /// starting a second, concurrent `connection.close()` — the external
    /// close, entering second. Because `closeDelay` only delays the first
    /// `close()` call, the external close's own `transport.close()`
    /// resumes essentially immediately — well before the refusal's,
    /// which is still asleep. If the ordering in `teardown(throwing:)`
    /// were reversed (`await transport.close()` before
    /// `finish(throwing:)`), the external close would reach `finish()`
    /// first, with `nil`, and the consumer would see a normal completion
    /// instead of the refusal's error — this test does fail under that
    /// reversed order, verified by hand while writing it.
    @Test
    func teardownPrefersTheFirstEntrantsErrorRegardlessOfWhichTransportCloseResumesFirst() async throws {
        let transport = InMemoryGatewayTransport(closeDelay: .milliseconds(300))
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"9.9"}"#)

        let refusalIsClosingTheTransport = await waitUntil(timeout: 2) {
            await transport.isCloseInFlight()
        }
        #expect(refusalIsClosingTheTransport)

        // Entering second, but — because the refusal's `transport.close()`
        // is still sleeping — resuming its own `transport.close()` first.
        await connection.close()

        var iterator = stream.makeAsyncIterator()
        await #expect(throws: WireVersion.CompatibilityError.mismatch(
            client: .current,
            host: WireVersion(major: 9, minor: 9)
        )) {
            _ = try await iterator.next()
        }
    }

    @Test
    func aMismatchedWireVersionErrorMessageNamesBothVersions() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"1.7"}"#)

        var iterator = stream.makeAsyncIterator()
        do {
            _ = try await iterator.next()
            Issue.record("expected the mismatch error to be thrown")
        } catch let error as WireVersion.CompatibilityError {
            #expect(error.message.contains("0.2"))
            #expect(error.message.contains("1.7"))
        }
    }

    @Test
    func anAttachedFrameWithNoWireFieldIsRefusedAsNotAdvertised() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5}"#)

        var iterator = stream.makeAsyncIterator()
        await #expect(throws: WireVersion.CompatibilityError.notAdvertised(client: .current)) {
            _ = try await iterator.next()
        }
    }

    @Test
    func anAttachedFrameWithAnUnparsableWireVersionIsRefused() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"banana"}"#)

        var iterator = stream.makeAsyncIterator()
        await #expect(throws: WireVersion.CompatibilityError.unparsable(client: .current, raw: "banana")) {
            _ = try await iterator.next()
        }
    }

    /// The C# side's `ProtocolVersion.MajorMinor` truncates a three-part
    /// version string; this client must not mirror that on the
    /// enforcement path either — a three-part advertisement is refused
    /// by name, not silently truncated into a match.
    @Test
    func anAttachedFrameWithAThreeComponentWireVersionIsRefusedNotTruncated() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2.1"}"#)

        var iterator = stream.makeAsyncIterator()
        await #expect(throws: WireVersion.CompatibilityError.unparsable(client: .current, raw: "0.2.1")) {
            _ = try await iterator.next()
        }
    }

    @Test
    func aMajorOnlyVersionDifferenceOnAttachedIsRefused() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"1.2"}"#)

        var iterator = stream.makeAsyncIterator()
        await #expect(throws: WireVersion.CompatibilityError.mismatch(
            client: .current,
            host: WireVersion(major: 1, minor: 2)
        )) {
            _ = try await iterator.next()
        }
    }

    @Test
    func aMinorOnlyVersionDifferenceOnAttachedIsRefused() async throws {
        let transport = InMemoryGatewayTransport()
        let connection = GatewayConnection(transport: transport)
        let stream = try await connection.connect()

        await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.9"}"#)

        var iterator = stream.makeAsyncIterator()
        await #expect(throws: WireVersion.CompatibilityError.mismatch(
            client: .current,
            host: WireVersion(major: 0, minor: 9)
        )) {
            _ = try await iterator.next()
        }
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
