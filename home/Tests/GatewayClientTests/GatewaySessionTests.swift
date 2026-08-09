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

/// A transport factory whose `makeTransport` closure genuinely suspends — parking on a
/// `CheckedContinuation` until a test explicitly calls `release()` — rather than merely
/// *converting* a synchronous closure to `GatewaySession`'s widened `async throws` signature
/// the way `RecordingTransportFactory` and `DelayedFirstTransportFactory` both do. B5 widened
/// `GatewaySession.init(makeTransport:)` specifically so a caller can suspend inside it
/// (resolving a device-key credential — Keychain and file I/O — before returning a
/// transport); this is the one double in this suite built to actually exercise that
/// suspension rather than merely type-check against it.
private final class GatedTransportFactory: Sendable {
    private let parkedContinuation = OSAllocatedUnfairLock<CheckedContinuation<Void, Never>?>(initialState: nil)
    private let createdTransports = OSAllocatedUnfairLock(initialState: [InMemoryGatewayTransport]())

    /// `true` once a `makeTransport()` call is genuinely parked awaiting `release()` — the
    /// condition a test polls for (via `waitUntil`) before racing a second call against the
    /// suspension. `false` again the instant `release()` runs.
    func hasEnteredAndIsParked() -> Bool {
        parkedContinuation.withLock { $0 != nil }
    }

    /// Resumes whichever `makeTransport()` call is currently parked, letting it build and
    /// return a fresh transport. A no-op if none is parked.
    func release() {
        let continuation = parkedContinuation.withLock { box -> CheckedContinuation<Void, Never>? in
            defer { box = nil }
            return box
        }
        continuation?.resume()
    }

    func transport(at index: Int) -> InMemoryGatewayTransport? {
        createdTransports.withLock { transports in
            transports.indices.contains(index) ? transports[index] : nil
        }
    }

    func transports() -> [InMemoryGatewayTransport] {
        createdTransports.withLock { $0 }
    }

    var makeTransport: @Sendable () async throws -> any GatewayTransport {
        {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                self.parkedContinuation.withLock { $0 = continuation }
            }
            let transport = InMemoryGatewayTransport()
            self.createdTransports.withLock { $0.append(transport) }
            return transport
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
///
/// `timeout` is a *readiness* deadline — "has the transport appeared yet" —
/// not a bound under test. Widening it (the default stays 2s) loosens
/// nothing any test asserts; it only affects how long a genuine failure
/// takes to surface. Contrast with a bound that discriminates (e.g. a
/// health-check timeout the test is specifically about), which must never
/// be widened to make a flake go away.
private func waitForTransport(_ factory: RecordingTransportFactory, at index: Int, timeout: TimeInterval = 2) async -> InMemoryGatewayTransport? {
    let appeared = await waitUntil(timeout: timeout) { factory.count() > index }
    guard appeared else { return nil }
    return factory.transport(at: index)
}

/// The `DelayedFirstTransportFactory` counterpart to the overload above.
/// See that overload's doc comment for what `timeout` is (and is not) for.
private func waitForTransport(_ factory: DelayedFirstTransportFactory, at index: Int, timeout: TimeInterval = 2) async -> InMemoryGatewayTransport? {
    let appeared = await waitUntil(timeout: timeout) { factory.count() > index }
    guard appeared else { return nil }
    return factory.transport(at: index)
}

/// The `GatedTransportFactory` counterpart to the overloads above.
/// See the first overload's doc comment for what `timeout` is (and is not) for.
private func waitForTransport(_ factory: GatedTransportFactory, at index: Int, timeout: TimeInterval = 2) async -> InMemoryGatewayTransport? {
    let appeared = await waitUntil(timeout: timeout) { factory.transports().count > index }
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
///
/// `timeout` is a readiness deadline, not a bound under test — see
/// `waitForTransport`'s doc comment on the same parameter.
private func waitForSentFrames(_ transport: InMemoryGatewayTransport, atLeast count: Int, timeout: TimeInterval = 2) async -> Bool {
    await waitUntil(timeout: timeout) { await transport.sentFrames().count >= count }
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
    /// exclusive, so a fresh session (`headSeq: 0`, matching the `lastSeq:
    /// 0` this attach itself sends — a brand new session has nothing to
    /// replay) followed by three yielded events must reattach with
    /// `lastSeq: 3`, not `0` (never advanced) and not `2` (off by one —
    /// inclusive instead of exclusive). Asserting the literal frame on the
    /// wire, not merely that *something* was sent, is what pins this rather
    /// than merely gesturing at it.
    ///
    /// The dedicated arithmetic for a reattach whose *own* `attached` frame
    /// carries a `headSeq` above the `lastSeq` it sent — the double-count
    /// bug (Blocker 1) this test's fixture used to mask by conflating
    /// `headSeq` with the seed — is
    /// `aReattachWhoseAttachedCarriesAHeadSeqAboveLastSeqReplaysExactlyThatManyEventsAndTheCursorLandsOnHeadSeq`
    /// below.
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
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

        let stream = try await handshake.value
        #expect(await session.lastObservedSeq == 0)

        await firstTransport.enqueue(#"{"type":"turn.delta","text":"one"}"#)
        await firstTransport.enqueue(#"{"type":"turn.delta","text":"two"}"#)
        await firstTransport.enqueue(#"{"type":"turn.delta","text":"three"}"#)

        var iterator = stream.makeAsyncIterator()
        _ = try await iterator.next()
        _ = try await iterator.next()
        _ = try await iterator.next()
        #expect(await session.lastObservedSeq == 3)

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
            #"{"gw":"attach","lastSeq":3,"sessionId":"s1"}"#
        ])

        await secondTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":3,"wire":"0.2"}"#)
        _ = try await reattachTask.value

        await session.close()
    }

    /// The scenario `reattachSendsTheHighestObservedSequencePlusOneAsLastSeq`
    /// above cannot cover, because every fixture in this suite — that one
    /// included — answers an attach with a `headSeq` equal to the `lastSeq`
    /// it just sent: a reattach whose own `attached` reply carries a
    /// `headSeq` genuinely *above* the `lastSeq` it sent, meaning the host
    /// has real backlog to replay before this reattach is caught up.
    ///
    /// This is the exact shape of the bug Blocker 1 fixed: seeding
    /// `lastObservedSeq` from `attachedFrame.headSeq` directly, rather than
    /// from the `lastSeq` this attach actually sent (clamped to `headSeq`),
    /// jumps the cursor to `headSeq` *before* the replay window's own events
    /// are counted — so by the time all of them are yielded, the cursor sits
    /// at `headSeq` plus the replay count, not at `headSeq`. The *next*
    /// reattach then sends a `lastSeq` above the host's own `headSeq`, which
    /// `SessionHandler.Attach`'s `Math.Clamp(lastSeq, 0, headSeq)`
    /// (`frontends/Dmon.Network/Sessions/SessionHandler.cs:251`) silently
    /// clips back down — silently dropping the entire gap between the two,
    /// with no error anywhere.
    ///
    /// Asserts both halves: after replaying exactly `headSeq - lastSeq`
    /// events, the cursor lands on `headSeq` (not `2 * headSeq - lastSeq`,
    /// the double-counted value the bug produced) — and a *second* reattach,
    /// immediately after, sends that same `headSeq` as its own `lastSeq`
    /// (not `2 * headSeq - lastSeq`), proving the corruption did not merely
    /// stay hidden in an unread property but would actually have desynced
    /// the wire.
    @Test
    func aReattachWhoseAttachedCarriesAHeadSeqAboveLastSeqReplaysExactlyThatManyEventsAndTheCursorLandsOnHeadSeq() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let firstTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(firstTransport, atLeast: 1)
        #expect(sent)
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

        let firstStream = try await handshake.value

        await firstTransport.simulateClose(code: .supersededByNewerAttach, reason: "test drop")
        var firstIterator = firstStream.makeAsyncIterator()
        await #expect(throws: GatewayTransportError.self) {
            _ = try await firstIterator.next()
        }

        // Reattach #1: sends `lastSeq: 0` (nothing observed yet), but the
        // host's own `attached` reply reports `headSeq: 5` — five events
        // this session missed while disconnected, still to be replayed.
        let firstReattachTask = Task {
            try await session.reattach()
        }

        let secondTransport = try #require(await waitForTransport(factory, at: 1))
        let secondSent = await waitForSentFrames(secondTransport, atLeast: 1)
        #expect(secondSent)
        #expect(await secondTransport.sentFrames() == [
            #"{"gw":"attach","lastSeq":0,"sessionId":"s1"}"#
        ])
        await secondTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":5,"wire":"0.2"}"#)

        let secondStream = try await firstReattachTask.value

        for index in 1...5 {
            await secondTransport.enqueue(#"{"type":"turn.delta","text":"replay \#(index)"}"#)
        }

        var secondIterator = secondStream.makeAsyncIterator()
        for _ in 1...5 {
            _ = try await secondIterator.next()
        }

        // Lands on `headSeq` (5), not `2 * headSeq - lastSeq` (10) — the
        // double-counted value the old, unfixed seed would have produced.
        #expect(await session.lastObservedSeq == 5)

        await secondTransport.simulateClose(code: .supersededByNewerAttach, reason: "test drop")
        await #expect(throws: GatewayTransportError.self) {
            _ = try await secondIterator.next()
        }

        // Reattach #2: must send `headSeq` (5) — not more.
        let thirdReattachTask = Task {
            try await session.reattach()
        }

        let thirdTransport = try #require(await waitForTransport(factory, at: 2))
        let thirdSent = await waitForSentFrames(thirdTransport, atLeast: 1)
        #expect(thirdSent)
        #expect(await thirdTransport.sentFrames() == [
            #"{"gw":"attach","lastSeq":5,"sessionId":"s1"}"#
        ])

        await thirdTransport.enqueue(#"{"gw":"attached","generation":3,"headSeq":5,"wire":"0.2"}"#)
        _ = try await thirdReattachTask.value

        await session.close()
    }

    /// A control frame — `ack` here, standing in for anything with a `gw`
    /// field — must never advance `lastObservedSeq`: only a `.event` item
    /// does. Interleaves one between two events (against a fresh, `headSeq:
    /// 0` session, so the seed itself contributes nothing to count) so a bug
    /// that counted every received frame, not just yielded events, would
    /// move the cursor to `3` instead of the correct `2` (two events, no
    /// `gw` frame counted) — pinned by then reattaching and reading the
    /// exact `lastSeq` on the wire, the same falsification discipline as
    /// the arithmetic test above.
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
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

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

        #expect(await session.lastObservedSeq == 2)

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
            #"{"gw":"attach","lastSeq":2,"sessionId":"s1"}"#
        ])

        await secondTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":2,"wire":"0.2"}"#)
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
    /// **A wrinkle recorded here when this test was first written, now
    /// re-reasoned rather than left stale (Blocker 1's section-supervisor
    /// remediation removed the exact mechanism this paragraph used to
    /// credit):** `routeFromPump`'s handshake branch no longer overwrites
    /// `lastObservedSeq` unconditionally on every `attached` frame — it now
    /// seeds it from the `lastSeq` *this* attach actually sent, clamped to
    /// `headSeq`, plus whatever this generation's own pre-`attached` replay
    /// already counted (`pendingReplayEventCount`). That seed is no longer
    /// an incidental reset that happens to erase *any* prior corruption on
    /// every reattach, regardless of its source — it is now a
    /// generation-local computation that only ever reads state this same
    /// call already owns. Concretely, in this test: `reattach()` still
    /// reads `lastSeq` from `self.lastObservedSeq` synchronously, before
    /// its first `await` — the same atomic-prelude discipline `attach`'s
    /// own doc comment describes — so a stale item from the *superseded*
    /// generation 1 pump can only reach `self.lastObservedSeq` at all if it
    /// is routed *before* that prelude runs; once `pumpGeneration` is
    /// bumped, the guard this test is really about (`generation ==
    /// pumpGeneration` in `routeFromPump`) is what keeps every later item
    /// from generation 1's backlog from touching `self.lastObservedSeq` a
    /// second time. In other words: the seed formula was never what made
    /// this scenario safe, and now that it can no longer coincidentally
    /// paper over a violation of the generation guard, the assertion below
    /// is a more honest — not a weaker — proof that the guard, not a
    /// convenient reset, is what this correctness actually rests on.
    ///
    /// This test is therefore a real, non-trivial regression check (a
    /// genuine backlog race against `close()`+`reattach()` leaves the
    /// resulting stream and cursor correct) but — honestly, and correctly
    /// scoped to *this* manifestation only — not a falsifying one: removing
    /// `routeFromPump`'s generation guard does not make it fail, for the
    /// same reason the `finishPump` door's removal does not.
    ///
    /// Flaky under a full suite run at roughly 1-in-3: the two waits after
    /// `enqueueBatch` below race the reattach connection's *appearance*
    /// against the pump draining the 50,000-frame backlog concurrently with
    /// `close()`/`reattach()`, under whatever load the rest of the suite
    /// (~41 other suites in parallel) puts on the machine. `waitUntil`'s
    /// default 2s readiness deadline is not enough headroom for that under
    /// load. The backlog is the actual stress this test exists to apply and
    /// stays exactly as sized; `backlogDrainReadinessTimeout` below only
    /// widens how long the two post-backlog waits are willing to give the
    /// reattach connection to appear, which the assertions this test makes
    /// (cursor correctness, no leaked backlog frame) do not depend on.
    @Test
    func aSupersededPumpsBufferedBacklogDoesNotCorruptTheStreamOrCursorAReattachJustInstalled() async throws {
        let factory = DelayedFirstTransportFactory(firstCloseDelay: .milliseconds(300))
        let session = GatewaySession(makeTransport: factory.makeTransport)

        // Readiness deadline for the two waits below, taken after the
        // 50,000-frame backlog is enqueued while `close()`/`reattach()` race
        // it. Unloaded this test completes in ~0.35s. 30s was chosen (not
        // the 15s floor) after direct evidence that 15s did not have
        // comfortable margin here: the first full-suite run taken right
        // after this file was recompiled failed exactly at the 15s mark
        // (a full-suite recompile plus 42 other suites competing for the
        // scheduler is worse than the recorded ~1-in-3 baseline, which was
        // measured against an already-built binary). Widening this costs
        // only how long a genuine failure takes to surface — it is a
        // readiness deadline ("has the transport appeared"), not a bound
        // this test's assertions depend on.
        let backlogDrainReadinessTimeout: TimeInterval = 30

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let firstTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(firstTransport, atLeast: 1)
        #expect(sent)
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

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

        let secondTransport = try #require(await waitForTransport(factory, at: 1, timeout: backlogDrainReadinessTimeout))
        let reattachSent = await waitForSentFrames(secondTransport, atLeast: 1, timeout: backlogDrainReadinessTimeout)
        #expect(reattachSent)
        await secondTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":0,"wire":"0.2"}"#)

        let secondStream = try await reattachResult
        _ = await closeResult

        // Not over-advanced by any backlog item that reached
        // `routeFromPump` after the generation bump — the reattach's own
        // `lastSeq` (0, nothing ever drained from the superseded stream)
        // clamped to its own `headSeq` (0) is the whole seed; nothing here
        // relies on a reset that also happens to erase corruption.
        #expect(await session.lastObservedSeq == 0)

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

    // MARK: - B5: a genuine suspension inside makeTransport()

    /// Review blocker: every test above this section races timing against a `makeTransport`
    /// that *type-checks* as `async throws` (`RecordingTransportFactory`,
    /// `DelayedFirstTransportFactory`) but never actually suspends inside its body — so none
    /// of them exercise the one behaviour B5's widened signature exists to permit: a caller
    /// genuinely awaiting something (Keychain/file I/O in the real
    /// `AuthenticatedTransportFactory`) before a transport, or even a `GatewayConnection`,
    /// exists at all. `GatedTransportFactory` forces that suspension for real.
    ///
    /// This test proves `attachInFlight`'s guard does not depend on the old suspension point
    /// (inside `connection.connect()`/awaiting `attached`, after a `GatewayConnection`
    /// already existed): `attachInFlight` is set synchronously at the top of
    /// `attach(sessionId:lastSeq:)`, *before* `performAttach` — and therefore before
    /// `makeTransport()` — is ever reached, so a second concurrent `attach` is refused here
    /// exactly as it was before B5, only now while the first call is parked earlier than any
    /// pre-B5 test could park it.
    @Test(.timeLimit(.minutes(1)))
    func aSecondAttachIsRefusedWhileTheFirstIsGenuinelySuspendedInsideMakeTransport() async throws {
        let factory = GatedTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let first = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let parked = await waitUntil { factory.hasEnteredAndIsParked() }
        #expect(parked)

        // Refused inside `attach`'s own synchronous prelude, before `makeTransport()` is ever
        // called for this second attempt — no second transport is created by this refusal.
        await #expect(throws: GatewaySessionError.attachAlreadyInFlight) {
            _ = try await session.attach(sessionId: "s2", lastSeq: 0)
        }
        #expect(factory.transports().isEmpty)

        factory.release()

        let attachTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(attachTransport, atLeast: 1)
        #expect(sent)
        await attachTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        _ = try await first.value
        #expect(await session.sessionId == "s1")
        #expect(await session.generation == 1)
        #expect(factory.transports().count == 1)

        await session.close()
    }

    /// The `reattach()` counterpart to the test above: `reattach()` checks `attachInFlight`
    /// *before* it ever inspects `sessionId` or `isAttached` (its own doc comment states this
    /// ordering), so it must be refused here for the same reason a second `attach` is — not
    /// merely because `sessionId` happens to still be `nil` while the first `attach` is
    /// parked inside `makeTransport()`.
    @Test(.timeLimit(.minutes(1)))
    func aReattachIsRefusedWhileAnAttachIsGenuinelySuspendedInsideMakeTransport() async throws {
        let factory = GatedTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let first = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let parked = await waitUntil { factory.hasEnteredAndIsParked() }
        #expect(parked)

        await #expect(throws: GatewaySessionError.attachAlreadyInFlight) {
            _ = try await session.reattach()
        }
        #expect(factory.transports().isEmpty)

        factory.release()

        let attachTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(attachTransport, atLeast: 1)
        #expect(sent)
        await attachTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":5,"wire":"0.2"}"#)

        _ = try await first.value

        await session.close()
    }

    /// The other half of the review blocker: `performAttach` reads `pumpGeneration` into its
    /// own `generation` local *after* `makeTransport()` returns — reached only once
    /// `self.connection`/`self.outputContinuation` are already set, both of which happen
    /// after the `try await makeTransport()` line — never before that suspension. This test
    /// forces a `close()` to land *during* that suspension, while `connection`, `pumpTask`,
    /// `outputContinuation`, and `attachWaiter` are all still `nil` (traced directly from
    /// `close()`'s own body: with all four `nil`, its only real effect is the `pumpGeneration`
    /// bump itself), and confirms the attach that was parked there still completes correctly
    /// once released — proof it picks up the *already-bumped* generation on resume rather than
    /// a value that went stale underneath it while it was suspended.
    ///
    /// **Falsified directly, not merely reasoned about — and the result narrows this test's
    /// own claim.** Reading `generation` from a local captured *before* `try await
    /// makeTransport()` instead of after was tried by hand: the interleaved `close()`'s bump
    /// then leaves that captured value stale by the time this method resumes, the pump it
    /// starts carries the stale generation, `routeFromPump(_:generation:)`'s guard silently
    /// discards the `attached` frame this test enqueues (`generation == pumpGeneration` is
    /// false), `attachWaiter` never resumes, and `first.value` below genuinely never returns
    /// — confirmed for real: the process sat parked, under 0.2% CPU, for over three and a
    /// half minutes (60+ times this test's normal ~0.01s) before being killed by hand, with
    /// no assertion failure and no output ever reaching the run log.
    ///
    /// **`.timeLimit(.minutes(1))` did not end that hang.** Both suspensions on the broken
    /// path — `GatedTransportFactory.makeTransport`'s plain `withCheckedContinuation`, and
    /// `first`'s own unstructured `Task` inside `attach`'s `withCheckedThrowingContinuation`
    /// — are un-cancellable: nothing calls `.resume()` on either, and Swift's cooperative
    /// cancellation cannot force a plain, non-cancellation-aware continuation to return, nor
    /// does cancelling one task reach into an unrelated `Task { … }` it merely happens to be
    /// awaiting the value of. Testing's time-limit trait cancels the *test's* task; it cannot
    /// reach either of those. So this test's guarantee is narrower than the trait's presence
    /// suggests: it genuinely diverges on the broken variant (fast pass vs. indefinite hang),
    /// which makes it a real regression check, but a real regression here would hang
    /// `dmon-home-test`'s run rather than fail it within a minute — the same gap likely
    /// applies to `aSecondConcurrentAttachIsRefusedWhileTheFirstIsStillInFlightAndTheFirstStillCompletesCorrectly`'s
    /// own `.timeLimit` claim above, not verified here, flagged for the Architect rather than
    /// silently corrected on a test outside this block's scope.
    @Test(.timeLimit(.minutes(1)))
    func aCloseDuringASuspendedAttachBumpsGenerationSafelyAndTheAttachStillCompletes() async throws {
        let factory = GatedTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let first = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let parked = await waitUntil { factory.hasEnteredAndIsParked() }
        #expect(parked)

        // `close()` does not check `attachInFlight` — nothing before B5 ever needed it to,
        // since there was no suspension this early for a concurrent call to land inside.
        await session.close()

        factory.release()

        let attachTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(attachTransport, atLeast: 1)
        #expect(sent)
        await attachTransport.enqueue(#"{"gw":"attached","generation":7,"headSeq":11,"wire":"0.2"}"#)

        _ = try await first.value
        #expect(await session.sessionId == "s1")
        #expect(await session.generation == 7)
        #expect(await session.headSeq == 11)

        await session.close()
    }

    // MARK: - Section 7 supervisor remediation

    /// Blocker 2: an event that reaches the wire *before* the `attached`
    /// reply answering the same attach — the race
    /// `NetworkConnectionEndpoint.cs:299-303` describes (`Attach()` has
    /// already released the pump's wake before the connection's serialized
    /// send funnel gets around to sending `attached`, so a buffered replay
    /// event can genuinely drain first on the same socket) — must reach
    /// this session's consumer, in order, and be counted, not silently
    /// dropped as this type used to do while `attachWaiter` was still set.
    ///
    /// Asserts both halves: `lastObservedSeq` already reflects the
    /// pre-`attached` event once `attach(sessionId:lastSeq:)` itself
    /// returns (proving it was counted, not merely queued), and the first
    /// item the returned stream ever yields is that event, not the
    /// `attached` frame itself (which stays handshake protocol, never
    /// session content — `theSessionStreamOutlivesTheHandshakeAndDeliversItemsThatArriveAfterAttached`
    /// above already pins that `attached` is never yielded for the
    /// ordinary, non-racing order; this pins the same thing for the racing
    /// one).
    @Test
    func anEventEnqueuedBeforeTheAttachedFrameReachesTheConsumerInOrderAndIsCounted() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let attachTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(attachTransport, atLeast: 1)
        #expect(sent)

        // Enqueued in this order — the racing event first, `attached`
        // second — so the pump processes the event while `attachWaiter` is
        // still set, exactly as `NetworkConnectionEndpoint.cs:299-303`
        // describes.
        let racingEvent = #"{"type":"turn.delta","text":"raced ahead of attached"}"#
        await attachTransport.enqueue(racingEvent)
        await attachTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":1,"wire":"0.2"}"#)

        let stream = try await handshake.value
        #expect(await session.lastObservedSeq == 1)

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()
        #expect(item == .event(racingEvent))

        await session.close()
    }

    /// Blocker 3: a `reattach()` whose own connection ends before its
    /// `attached` reply ever arrives — the ordinary transient failure
    /// `reattach()` exists to recover from, not a reason to forget which
    /// session this actor was resuming — must leave `sessionId` intact so a
    /// *later* `reattach()` can still succeed. The old behaviour nilled
    /// `sessionId` on exactly this failure, which turned every subsequent
    /// `reattach()` into `.reattachWithoutPriorAttach` — a permanent,
    /// misdescribed dead end after one transient drop.
    @Test
    func aFailedReattachPreservesSessionIdSoALaterReattachCanStillSucceed() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let firstTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(firstTransport, atLeast: 1)
        #expect(sent)
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

        let firstStream = try await handshake.value
        await firstTransport.simulateClose(code: .supersededByNewerAttach, reason: "test drop")
        var firstIterator = firstStream.makeAsyncIterator()
        await #expect(throws: GatewayTransportError.self) {
            _ = try await firstIterator.next()
        }

        // First reattach attempt: its connection ends before `attached`
        // ever arrives.
        let failedReattach = Task {
            try await session.reattach()
        }

        let failedReattachTransport = try #require(await waitForTransport(factory, at: 1))
        let failedSent = await waitForSentFrames(failedReattachTransport, atLeast: 1)
        #expect(failedSent)
        await failedReattachTransport.close()

        await #expect(throws: GatewaySessionError.connectionClosedBeforeAttached) {
            _ = try await failedReattach.value
        }

        // The regression this test exists for: under the old behaviour,
        // this was already `nil`.
        #expect(await session.sessionId == "s1")

        let secondReattach = Task {
            try await session.reattach()
        }

        let secondTransport = try #require(await waitForTransport(factory, at: 2))
        let secondSent = await waitForSentFrames(secondTransport, atLeast: 1)
        #expect(secondSent)
        await secondTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":0,"wire":"0.2"}"#)

        _ = try await secondReattach.value
        #expect(await session.sessionId == "s1")

        await session.close()
    }

    /// Blocker 4: `attach(sessionId:lastSeq:)`, not just `reattach()`, is
    /// legal to call over a *dropped-but-not-`close()`d* connection — only
    /// `isAttached` gates it, and a natural drop already clears that inside
    /// `finishPump(throwing:generation:)` without ever touching
    /// `self.connection` itself. Before this fix, only `reattach()` closed
    /// the stale connection it was superseding; a plain `attach` overwrote
    /// `self.connection` outright, orphaning the old connection's read loop
    /// and transport — the same leak shape as
    /// `tech-debt/websocket-receive-cancellation-leak.md`.
    ///
    /// Pins it directly against the stale transport's own observable
    /// state, not merely "the call did not crash": `isClosedLocally()` must
    /// be `true` once the second `attach` has completed.
    @Test
    func attachOverADroppedButNotNilConnectionClosesTheStaleOneRatherThanOrphaningIt() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let handshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let firstTransport = try #require(await waitForTransport(factory, at: 0))
        let sent = await waitForSentFrames(firstTransport, atLeast: 1)
        #expect(sent)
        await firstTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

        let firstStream = try await handshake.value

        // A natural drop: `isAttached` clears, but `self.connection` is
        // left non-nil — `finishPump` never touches it; only `close()` and
        // `reattach()` do.
        await firstTransport.simulateClose(code: .supersededByNewerAttach, reason: "test drop")
        var firstIterator = firstStream.makeAsyncIterator()
        await #expect(throws: GatewayTransportError.self) {
            _ = try await firstIterator.next()
        }

        #expect(await firstTransport.isClosedLocally() == false)

        // A plain `attach`, not `reattach()`, over that same post-drop
        // state.
        let secondHandshake = Task {
            try await session.attach(sessionId: "s1", lastSeq: 0)
        }

        let secondTransport = try #require(await waitForTransport(factory, at: 1))
        let secondSent = await waitForSentFrames(secondTransport, atLeast: 1)
        #expect(secondSent)
        await secondTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":0,"wire":"0.2"}"#)

        _ = try await secondHandshake.value

        #expect(await firstTransport.isClosedLocally())

        await session.close()
    }
}
