import Foundation
import Testing
import os
@testable import GatewayClient

/// A transport factory for `aSupersededPumpsLateCompletionDoesNotFinishTheStreamAReattachJustInstalled`
/// only: hands out one `InMemoryGatewayTransport(closeDelay:)` for its
/// first call, then a fresh, undecorated `InMemoryGatewayTransport` for
/// every call after — while still recording every transport it creates,
/// in order, so `waitForTransport` works against it exactly as it does
/// against `RecordingTransportFactory`. Not folded into
/// `RecordingTransportFactory` itself: every other test in this suite
/// wants identically-configured transports, and threading an
/// only-sometimes-used `closeDelay` through it would obscure that.
private final class DelayedFirstTransportFactory: Sendable {
    private let state = OSAllocatedUnfairLock(initialState: [InMemoryGatewayTransport]())
    private let firstCloseDelay: Duration

    init(firstCloseDelay: Duration) {
        self.firstCloseDelay = firstCloseDelay
    }

    var makeTransport: @Sendable () -> any GatewayTransport {
        { [self] in
            state.withLock { transports in
                let transport = transports.isEmpty
                    ? InMemoryGatewayTransport(closeDelay: firstCloseDelay)
                    : InMemoryGatewayTransport()
                transports.append(transport)
                return transport
            }
        }
    }

    func count() -> Int {
        state.withLock { $0.count }
    }

    func transport(at index: Int) -> InMemoryGatewayTransport? {
        state.withLock { transports in
            transports.indices.contains(index) ? transports[index] : nil
        }
    }
}

/// Polls `condition` until it is true or `timeout` elapses. Local to this
/// suite rather than shared, matching `GatewayConnectionTests`' own
/// precedent (its doc comment on `waitUntil` explains why a one-off
/// polling loop like this is not worth sharing across test targets).
private func waitUntil(timeout: TimeInterval = 2, condition: @escaping @Sendable () async -> Bool) async -> Bool {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if await condition() { return true }
        try? await Task.sleep(nanoseconds: 5_000_000)
    }
    return await condition()
}

/// Waits for `factory` to have created a transport at `index`.
private func waitForTransport(_ factory: RecordingTransportFactory, at index: Int) async -> InMemoryGatewayTransport? {
    let appeared = await waitUntil { factory.count() > index }
    guard appeared else { return nil }
    return factory.transport(at: index)
}

/// The `DelayedFirstTransportFactory` counterpart to the overload above.
private func waitForTransport(_ factory: DelayedFirstTransportFactory, at index: Int) async -> InMemoryGatewayTransport? {
    let appeared = await waitUntil { factory.count() > index }
    guard appeared else { return nil }
    return factory.transport(at: index)
}

/// Waits until `transport` has sent at least `count` frames. Used before a
/// test calls `transport.close()` directly (bypassing `GatewayConnection`)
/// to simulate "the connection just ended": `InMemoryGatewayTransport
/// .close()` is a no-op until `connect()` has run
/// (`hasConnected`), and `send(_:)` only ever completes after `connect()`
/// has — so a sent frame is proof `connect()` has already happened, which
/// polling `count()` alone (the transport exists) is not.
private func waitForSentFrames(_ transport: InMemoryGatewayTransport, atLeast count: Int) async -> Bool {
    await waitUntil { await transport.sentFrames().count >= count }
}

/// Exercises `GatewaySession`'s create→attach handshake (tasks 7.1/7.2):
/// `create` and `attach` each go out on their own connection
/// (`RecordingTransportFactory` proves this), `generation`/`headSeq` are
/// recorded from `attached`, and `createRejected` surfaces as an
/// actionable, distinguishable error that never leads to an `attach`.
@Suite
struct GatewaySessionTests {
    @Test
    func createIsFollowedByAttachUsingTheSessionIdFromCreatedNotAHardcodedValue() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.start(agent: nil)
        }

        let createTransport = try #require(await waitForTransport(factory, at: 0))
        // Deliberately not the same literal a hard-coded-session-id bug
        // would still pass with — this value only reaches the `attach`
        // frame if `attach(sessionId:lastSeq:)` actually reads it back out
        // of `created`.
        await createTransport.enqueue(#"{"gw":"created","sessionId":"session-from-created-reply"}"#)

        let attachTransport = try #require(await waitForTransport(factory, at: 1))
        await attachTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        _ = try await handshake.value

        #expect(factory.count() == 2)
        #expect(await createTransport.sentFrames() == [#"{"gw":"create"}"#])
        #expect(await attachTransport.sentFrames() == [
            #"{"gw":"attach","lastSeq":0,"sessionId":"session-from-created-reply"}"#
        ])
        #expect(await session.sessionId == "session-from-created-reply")
        #expect(await session.generation == 1)
        #expect(await session.headSeq == 5)
    }

    @Test
    func createRejectedSurfacesItsCodeAndMessageAndNeverAttaches() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.start(agent: nil)
        }

        let createTransport = try #require(await waitForTransport(factory, at: 0))
        await createTransport.enqueue(
            #"{"gw":"createRejected","code":"cap_reached","message":"too many sessions"}"#
        )

        await #expect(throws: GatewaySessionError.createRejected(
            code: "cap_reached",
            message: "too many sessions"
        )) {
            _ = try await handshake.value
        }

        // Both halves of the claim: no second connection was ever opened,
        // and no transport anywhere ever sent an `attach` frame — the
        // second check would still catch a bug that reused the create
        // connection to attach instead of opening a new one.
        #expect(factory.count() == 1)
        for transport in factory.transports() {
            let sent = await transport.sentFrames()
            #expect(!sent.contains { $0.contains(#""gw":"attach""#) })
        }
    }

    @Test
    func anADR003ErrorEventDuringCreateIsNotMistakenForCreateRejected() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.createSession(agent: nil)
        }

        let createTransport = try #require(await waitForTransport(factory, at: 0))
        // No `gw` field: an ADR-003 event, not a control frame. If this
        // were ever mistaken for `createRejected` the call below would
        // throw instead of returning the id from the `created` reply that
        // follows it.
        await createTransport.enqueue(#"{"type":"error","message":"turn failed"}"#)
        await createTransport.enqueue(#"{"gw":"created","sessionId":"after-the-stray-event"}"#)

        let sessionId = try await handshake.value

        #expect(sessionId == "after-the-stray-event")
    }

    @Test
    func theConnectionEndingBeforeCreatedThrowsADistinctError() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.createSession(agent: nil)
        }

        let createTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(createTransport, atLeast: 1)
        #expect(sent)
        await createTransport.close()

        await #expect(throws: GatewaySessionError.connectionClosedBeforeCreated) {
            _ = try await handshake.value
        }
    }

    @Test
    func theConnectionEndingBeforeAttachedThrowsADistinctError() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let attachTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(attachTransport, atLeast: 1)
        #expect(sent)
        await attachTransport.close()

        await #expect(throws: GatewaySessionError.connectionClosedBeforeAttached) {
            _ = try await handshake.value
        }
    }

    /// Pins that the two "connection ended" cases are genuinely
    /// distinguishable from each other, not merely both present in the
    /// enum — `theConnectionEndingBeforeCreatedThrowsADistinctError` and
    /// this test would both still pass against a version that returned the
    /// same case for either phase.
    @Test
    func theTwoConnectionEndedErrorsAreNotEqualToEachOther() {
        #expect(
            GatewaySessionError.connectionClosedBeforeCreated
                != GatewaySessionError.connectionClosedBeforeAttached
        )
    }

    @Test
    func aPeerCloseDuringCreateReachesTheCallerWithItsCloseCodeIntact() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.createSession(agent: nil)
        }

        let createTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(createTransport, atLeast: 1)
        #expect(sent)
        // 4500 is a real server path for a create that fails outside the
        // three `createRejected` codes (`CreateHandshakeTimeoutSeconds`
        // expiring, or the core failing to spawn) — this must reach the
        // caller as the close code itself, not a generic failure.
        await createTransport.simulateClose(code: .coreFailure, reason: "session create failed")

        await #expect(throws: GatewayTransportError.closed(
            code: .coreFailure,
            reason: "session create failed"
        )) {
            _ = try await handshake.value
        }
    }

    @Test
    func aWireVersionMismatchOnAttachedSurfacesAsCompatibilityErrorUnwrapped() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let attachTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(attachTransport, atLeast: 1)
        #expect(sent)
        await attachTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"9.9"}"#)

        await #expect(throws: WireVersion.CompatibilityError.mismatch(
            client: .current,
            host: WireVersion(major: 9, minor: 9)
        )) {
            _ = try await handshake.value
        }

        #expect(await session.generation == nil)
        #expect(await session.headSeq == nil)
    }

    @Test
    func theSessionStreamOutlivesTheHandshakeAndDeliversItemsThatArriveAfterAttached() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let attachTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(attachTransport, atLeast: 1)
        #expect(sent)
        await attachTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        let stream = try await handshake.value

        let laterEvent = #"{"type":"turn.delta","text":"after attach"}"#
        await attachTransport.enqueue(laterEvent)

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()

        #expect(item == .event(laterEvent))

        await session.close()
    }

    /// The regression this review round exists for: a second `attach`
    /// arriving while the first is genuinely still in flight (its
    /// `attached` reply not yet enqueued) must be refused, and the first
    /// call must still complete with *its own* `generation`/`headSeq` —
    /// not a value a buggy cross-wired resume would have handed to the
    /// wrong caller. `factory.count() == 1` additionally proves the second
    /// call never opened a connection of its own: it was refused inside
    /// `attach`'s synchronous prelude, before `performAttach` ever runs.
    ///
    /// `.timeLimit(.minutes(1))`: verified by hand that regressing the
    /// `attachInFlight` guard does not make this test fail an assertion —
    /// it hangs forever instead (the second call's own `attach` overwrites
    /// `attachWaiter`, leaking the first call's continuation with nothing
    /// left to resume it). A bound this generous compared to this test's
    /// normal sub-millisecond runtime only ever fires on that regression,
    /// and turns "this job never finishes" into a failing test once
    /// `dmon-home-test` is wired into CI (section 10) — do not remove it as
    /// unnecessary noise.
    @Test(.timeLimit(.minutes(1)))
    func aSecondConcurrentAttachIsRefusedWhileTheFirstIsStillInFlightAndTheFirstStillCompletesCorrectly() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let first = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let attachTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(attachTransport, atLeast: 1)
        #expect(sent)

        // `first` is still parked awaiting `attached` — nothing has been
        // enqueued for it yet — so this genuinely races the in-flight
        // handshake rather than merely following it.
        await #expect(throws: GatewaySessionError.attachAlreadyInFlight) {
            _ = try await session.attach(sessionId: "s2", lastSeq: 0)
        }

        await attachTransport.enqueue(#"{"gw":"attached","generation":7,"headSeq":11,"wire":"0.2"}"#)

        _ = try await first.value

        #expect(await session.sessionId == "s1")
        #expect(await session.generation == 7)
        #expect(await session.headSeq == 11)
        #expect(factory.count() == 1)

        await session.close()
    }

    /// Pins what `close()`'s doc comment claims about racing a pending
    /// handshake, rather than leaving it merely reasoned about: closing
    /// the session while `attach` is still awaiting `attached` must unblock
    /// that call with `.connectionClosedBeforeAttached`, not hang it
    /// forever.
    @Test
    func closingWhileAnAttachHandshakeIsStillPendingUnblocksItWithADistinctError() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let attachTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(attachTransport, atLeast: 1)
        #expect(sent)

        await session.close()

        await #expect(throws: GatewaySessionError.connectionClosedBeforeAttached) {
            _ = try await handshake.value
        }
    }

    /// Pins the exact arithmetic task 7.3 depends on: `lastSeq` is
    /// exclusive, so a `headSeq` of `5` followed by three yielded events
    /// must reattach with `lastSeq: 8`, not `5` (never advanced) and not
    /// `7` (off by one — inclusive instead of exclusive). Asserting the
    /// literal frame on the wire, not merely that *something* was sent, is
    /// what pins this rather than merely gesturing at it.
    @Test
    func reattachSendsTheHighestObservedSequencePlusOneAsLastSeq() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let firstTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(firstTransport, atLeast: 1)
        #expect(sent)
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        let stream = try await handshake.value
        #expect(await session.lastObservedSeq == 5)

        await firstTransport.enqueue(#"{"type":"turn.delta","text":"one"}"#)
        await firstTransport.enqueue(#"{"type":"turn.delta","text":"two"}"#)
        await firstTransport.enqueue(#"{"type":"turn.delta","text":"three"}"#)

        var iterator = stream.makeAsyncIterator()
        _ = try await iterator.next()
        _ = try await iterator.next()
        _ = try await iterator.next()
        #expect(await session.lastObservedSeq == 8)

        await firstTransport.simulateClose(code: .supersededByNewerAttach, reason: "test drop")
        await #expect(throws: GatewayTransportError.self) {
            _ = try await iterator.next()
        }

        let reattachTask = Task {
            try await session.reattach()
        }

        let secondTransport = try #require(await waitForTransport(factory, at: 1))
        let reattachSent = await waitForSentFrames(secondTransport, atLeast: 1)
        #expect(reattachSent)
        #expect(await secondTransport.sentFrames() == [
            #"{"gw":"attach","lastSeq":8,"sessionId":"s1"}"#
        ])

        await secondTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":8,"wire":"0.2"}"#)
        _ = try await reattachTask.value

        await session.close()
    }

    /// A control frame — `ack` here, standing in for anything with a `gw`
    /// field — must never advance `lastObservedSeq`: only a `.event` item
    /// does. Interleaves one between two events so a bug that counted every
    /// received frame, not just yielded events, would move the cursor to
    /// `7` instead of the correct `6` (`5` + two events, no `gw` frame
    /// counted) — pinned by then reattaching and reading the exact
    /// `lastSeq` on the wire, the same falsification discipline as the
    /// arithmetic test above.
    @Test
    func controlFramesInterleavedAmongEventsDoNotAdvanceTheCursor() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let firstTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(firstTransport, atLeast: 1)
        #expect(sent)
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        let stream = try await handshake.value

        await firstTransport.enqueue(#"{"type":"turn.delta","text":"one"}"#)
        await firstTransport.enqueue(#"{"gw":"ack","id":"abc"}"#)
        await firstTransport.enqueue(#"{"type":"turn.delta","text":"two"}"#)

        var iterator = stream.makeAsyncIterator()
        let first = try await iterator.next()
        #expect(first == .event(#"{"type":"turn.delta","text":"one"}"#))
        let second = try await iterator.next()
        #expect(second == .control(.ack(AckFrame(id: "abc"))))
        let third = try await iterator.next()
        #expect(third == .event(#"{"type":"turn.delta","text":"two"}"#))

        #expect(await session.lastObservedSeq == 7)

        await firstTransport.simulateClose(code: .supersededByNewerAttach, reason: "test drop")
        await #expect(throws: GatewayTransportError.self) {
            _ = try await iterator.next()
        }

        let reattachTask = Task {
            try await session.reattach()
        }

        let secondTransport = try #require(await waitForTransport(factory, at: 1))
        let reattachSent = await waitForSentFrames(secondTransport, atLeast: 1)
        #expect(reattachSent)
        #expect(await secondTransport.sentFrames() == [
            #"{"gw":"attach","lastSeq":7,"sessionId":"s1"}"#
        ])

        await secondTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":7,"wire":"0.2"}"#)
        _ = try await reattachTask.value

        await session.close()
    }

    /// D1: `reattach()` cannot hand a caller back the stream a prior
    /// `attach`/`reattach` returned — that stream already ended, carrying
    /// the transport's own error, by the time `reattach()` is even
    /// callable (D3's gate). Pins both halves: the *old* stream is already
    /// finished (with the drop's own error, not silently), and the *new*
    /// one is a distinct object that still delivers items.
    @Test
    func reattachReturnsANewStreamLeavingThePriorOneAlreadyEndedWithTheDropsError() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let firstTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(firstTransport, atLeast: 1)
        #expect(sent)
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        let firstStream = try await handshake.value

        await firstTransport.simulateClose(code: .supersededByNewerAttach, reason: "test drop")

        var firstIterator = firstStream.makeAsyncIterator()
        await #expect(throws: GatewayTransportError.closed(
            code: .supersededByNewerAttach,
            reason: "test drop"
        )) {
            _ = try await firstIterator.next()
        }

        let reattachTask = Task {
            try await session.reattach()
        }

        let secondTransport = try #require(await waitForTransport(factory, at: 1))
        let reattachSent = await waitForSentFrames(secondTransport, atLeast: 1)
        #expect(reattachSent)
        await secondTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":5,"wire":"0.2"}"#)

        let secondStream = try await reattachTask.value

        // The old stream is still, permanently, ended — a later drop's
        // teardown must never resurrect it.
        var stillFirstIterator = firstStream.makeAsyncIterator()
        let stillNilOrThrows: Bool
        do {
            let item = try await stillFirstIterator.next()
            stillNilOrThrows = item == nil
        } catch {
            stillNilOrThrows = true
        }
        #expect(stillNilOrThrows)

        // The new stream is distinct and live.
        let eventRaw = #"{"type":"turn.delta","text":"after reattach"}"#
        await secondTransport.enqueue(eventRaw)
        var secondIterator = secondStream.makeAsyncIterator()
        let item = try await secondIterator.next()
        #expect(item == .event(eventRaw))

        await session.close()
    }

    @Test
    func reattachWithoutAnyPriorAttachIsRefused() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        await #expect(throws: GatewaySessionError.reattachWithoutPriorAttach) {
            _ = try await session.reattach()
        }
        #expect(factory.count() == 0)
    }

    /// D3: reattach is refused while the connection is still live, by
    /// construction — the task is explicit that reattach is for *after* a
    /// dropped connection, and forcing a live stream closed would discard
    /// counted-but-undrained events.
    @Test
    func reattachWhileTheConnectionIsStillLiveIsRefused() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let firstTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(firstTransport, atLeast: 1)
        #expect(sent)
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        _ = try await handshake.value

        await #expect(throws: GatewaySessionError.reattachWhileAttached) {
            _ = try await session.reattach()
        }
        // Refused before opening anything.
        #expect(factory.count() == 1)

        await session.close()
    }

    /// The block's highest-risk property (D2): a pump superseded by
    /// `close()`'s own generation bump, but whose *own* completion is a
    /// separate, later event (its underlying connection stream ending only
    /// once that connection is actually torn down), must not be able to
    /// finish a stream a later `reattach()` has *already* installed.
    ///
    /// `close()` finishes `firstStream` — and flips `isAttached` false —
    /// directly and synchronously, *before* it ever awaits
    /// `firstTransport`'s own (delayed, via `closeDelay`) teardown; see
    /// `close()`'s own doc comment. Running it as its own `Task`, rather
    /// than `await`-ing it inline, is what lets this test observe that:
    /// `firstStream` ends, and `reattach()` becomes callable, long before
    /// `closeTask` itself returns. Only once `closeTask` actually reaches
    /// `firstTransport`'s teardown does `firstTransport`'s underlying
    /// connection stream finish — which is what wakes the *original* pump
    /// (still parked mid-loop, iterating that stream, entirely unaware
    /// `close()` has moved on) and drives it to call
    /// `finishPump(throwing:generation:)` with its own, by-then stale
    /// `generation: 0` — confirmed by temporarily instrumenting both
    /// methods while writing this test: that call reliably happens, with
    /// `pumpGeneration` already bumped past it.
    ///
    /// **What this test does not, and — as far as extensive hand-testing
    /// could establish — cannot, pin under this transport's timing.**
    /// `GatewayConnection.teardown(throwing:)` calls `finish(throwing:)`
    /// *before* its own `await transport.close()` (that ordering is the
    /// whole reason `close()`'s effects are observable this early at all;
    /// see its own doc comment) — so the stale pump's wake-up is not
    /// gated by `closeDelay` at all, only the *outer* `close()` call's own
    /// return is. Racing `reattach()` concurrently against `closeTask`
    /// (tried with `Task(priority:)` hints and with the original pump
    /// artificially fed hundreds of buffered events first, to lengthen its
    /// own path) still consistently lost to the stale notice: resuming an
    /// already-parked consumer is structurally fewer actor hops than
    /// `reattach()`'s own connect-send-install round trip on a brand new
    /// connection, so the stale call was observed, every time, arriving
    /// *before* `performAttach(sessionId:lastSeq:)` had even reached its
    /// own `self.outputContinuation = outputContinuation` line — never
    /// after. Under that ordering the stale call's guard check is not
    /// load-bearing for *this specific interleaving* (`outputContinuation`
    /// is already `nil`, so `outputContinuation?.finish()` is inert with
    /// or without it) — this test cannot, by itself, prove the guard is
    /// necessary by observing a failure with it removed. Flagged to the
    /// Architect rather than asserted away.
    ///
    /// What this test *does* still pin, and does exercise for real: a
    /// genuinely stale, mismatched `finishPump(throwing:generation:)` call
    /// reaches the guard (not a fabricated one), `reattach()` succeeds and
    /// returns usable state despite racing a superseded pump's real
    /// teardown, and the resulting stream is fully functional afterward.
    ///
    /// Scoped deliberately to the `finishPump` door only — a stale pump
    /// *finishing* a stream it does not own. The separate, worse-shaped
    /// door of a stale pump *yielding buffered backlog* into one is
    /// `routeFromPump`'s to guard, and is attempted on its own terms by
    /// `aSupersededPumpsBufferedBacklogDoesNotCorruptTheStreamOrCursorAReattachJustInstalled`
    /// below — this test's negative result says nothing about that one.
    @Test
    func aSupersededPumpsLateCompletionDoesNotFinishTheStreamAReattachJustInstalled() async throws {
        let factory = DelayedFirstTransportFactory(firstCloseDelay: .milliseconds(300))
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let firstTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(firstTransport, atLeast: 1)
        #expect(sent)
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        let firstStream = try await handshake.value

        let parked = await waitUntil { await firstTransport.isReceiverWaiting() }
        #expect(parked)

        let closeTask = Task {
            await session.close()
        }

        // `close()`'s own direct `finishPump` call ends `firstStream`
        // immediately — well before `closeTask` itself returns, since that
        // call has not yet reached `firstTransport`'s delayed teardown.
        var firstIterator = firstStream.makeAsyncIterator()
        let itemAfterClose = try await firstIterator.next()
        #expect(itemAfterClose == nil)

        let reattachTask = Task {
            try await session.reattach()
        }

        let secondTransport = try #require(await waitForTransport(factory, at: 1))
        let reattachSent = await waitForSentFrames(secondTransport, atLeast: 1)
        #expect(reattachSent)
        await secondTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":5,"wire":"0.2"}"#)

        let secondStream = try await reattachTask.value

        // Forces `firstTransport`'s delayed teardown — and with it, the
        // original pump's own late, stale `finishPump` call — to have
        // already happened before the assertion below runs.
        await closeTask.value

        let secondEventRaw = #"{"type":"turn.delta","text":"still alive after reattach"}"#
        await secondTransport.enqueue(secondEventRaw)
        var secondIterator = secondStream.makeAsyncIterator()
        let secondEvent = try await secondIterator.next()
        #expect(secondEvent == .event(secondEventRaw))

        await session.close()
    }

    /// A second, distinct manifestation of the same generation gap
    /// (flagged in review, not originally attempted): `routeFromPump` is
    /// generation-scoped too, and its hazard is worse than a spurious
    /// `finish` — a stale pump *yielding buffered backlog* into a newly
    /// installed stream would silently over-advance `lastObservedSeq` past
    /// what the new consumer actually received, corrupting the very cursor
    /// this requirement rests on.
    ///
    /// Reachability (traced in review): impossible via a *natural* drop —
    /// `isAttached` only flips inside `finishPump`, which only runs once
    /// `runPump`'s loop has already exhausted the connection's buffered
    /// items, so nothing is left stale by the time `reattach()` could ever
    /// see `!isAttached`. It is `close()`'s own reorder (this block's
    /// deviation, judged sound in review) that opens the door: `close()`
    /// calls `finishPump` directly and synchronously, *before* awaiting the
    /// connection's teardown — decoupling `isAttached`'s flip from whether
    /// the superseded pump has actually drained. `pumpTask?.cancel()` does
    /// not stop an already-buffered `for await` loop (cancellation is
    /// cooperative; `AsyncThrowingStream` never checks it) — so a plain
    /// `close()` immediately followed by `reattach()`, with real backlog
    /// outstanding on the stale connection, is the reachable sequence.
    ///
    /// **Attempted, and — like the `finishPump` door — could not be forced
    /// to land after the new continuation is installed, across a wide
    /// sweep, not a single try.** `enqueueBatch(_:)` was added specifically
    /// so backlog could be delivered to the transport's inbox in one
    /// actor-isolated call (a loop of individual `enqueue(_:)` calls was
    /// tried first and drains in near lock-step with the loop instead of
    /// ever falling behind — confirmed by instrumentation, not assumed).
    /// Backlog sizes from 3,000 to 1,000,000 frames, combined with an
    /// additional delay before racing `close()`/`reattach()` — anywhere
    /// from zero to fifty `Task.yield()` calls, and real `Task.sleep`
    /// windows from 10 microseconds to 5 milliseconds — were all tried.
    /// Across every configuration: at most one single frame ever straddled
    /// the generation boundary at all (most runs: zero), and every one
    /// that did was still routed while `outputContinuation` was `nil` —
    /// i.e. still arriving *before* `reattach()`'s own `performAttach`
    /// reaches its install line, the same structural loss the `finishPump`
    /// door has (a fresh connect-send-install round trip is consistently
    /// slower than the stale pump resuming an already-parked consumer, or
    /// than `close()`'s own synchronous prelude winning first).
    ///
    /// **A real, newly-discovered wrinkle worth recording so it is not
    /// re-derived:** even instrumented with the guard removed, a stale
    /// item landing *before* the new generation's own `attached` frame
    /// does briefly corrupt `lastObservedSeq` — but `routeFromPump`'s
    /// handshake branch overwrites it unconditionally
    /// (`lastObservedSeq = attachedFrame.headSeq`) once that frame
    /// arrives, which in every observed run erased the corruption before
    /// `reattach()` ever returned. An assertion taken only after
    /// `reattach()` completes — the only point this actor exposes to a
    /// caller — would not have caught that transient corruption even with
    /// the guard removed. Flagged, not asserted around.
    ///
    /// This test is therefore a real, non-trivial regression check (a
    /// genuine backlog race against `close()`+`reattach()` leaves the
    /// resulting stream and cursor correct) but — honestly, and correctly
    /// scoped to *this* manifestation only — not a falsifying one: removing
    /// `routeFromPump`'s generation guard does not make it fail, for the
    /// same reason the `finishPump` door's removal does not.
    @Test
    func aSupersededPumpsBufferedBacklogDoesNotCorruptTheStreamOrCursorAReattachJustInstalled() async throws {
        let factory = DelayedFirstTransportFactory(firstCloseDelay: .milliseconds(300))
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let firstTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(firstTransport, atLeast: 1)
        #expect(sent)
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        _ = try await handshake.value

        let parked = await waitUntil { await firstTransport.isReceiverWaiting() }
        #expect(parked)

        // Delivered in one atomic call — see `enqueueBatch(_:)`'s doc
        // comment for why a loop of individual `enqueue(_:)` calls does
        // not produce genuine backlog (it drains in lock-step instead).
        let backlog = (0..<50_000).map { #"{"type":"turn.delta","text":"backlog \#($0)"}"# }
        await firstTransport.enqueueBatch(backlog)

        // `close()` and `reattach()` raced concurrently, immediately after
        // the backlog lands, rather than sequenced — the configuration the
        // sweep above found most likely (among many tried) to still have
        // backlog outstanding when the generation bump happens.
        async let closeResult: Void = session.close()
        async let reattachResult = session.reattach()

        let secondTransport = try #require(await waitForTransport(factory, at: 1))
        let reattachSent = await waitForSentFrames(secondTransport, atLeast: 1)
        #expect(reattachSent)
        await secondTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":5,"wire":"0.2"}"#)

        let secondStream = try await reattachResult
        _ = await closeResult

        // Not over-advanced by any backlog item that reached
        // `routeFromPump` after the generation bump.
        #expect(await session.lastObservedSeq == 5)

        // Not cross-contaminated: the first item this stream ever yields
        // must be the one explicitly sent to it, not a leaked backlog
        // frame from the superseded connection.
        let secondEventRaw = #"{"type":"turn.delta","text":"still alive after reattach"}"#
        await secondTransport.enqueue(secondEventRaw)
        var secondIterator = secondStream.makeAsyncIterator()
        let secondEvent = try await secondIterator.next()
        #expect(secondEvent == .event(secondEventRaw))

        await session.close()
    }
}
