import Foundation
import Testing
@testable import GatewayClient

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
}
