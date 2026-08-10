import Foundation
import Testing
import os
@testable import GatewayClient

/// Polls `condition` until it is true or `timeout` elapses. Duplicated
/// locally rather than shared, matching every other suite in this test
/// target's own precedent for this exact helper.
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

/// Waits until `transport` has sent at least `count` frames.
private func waitForSentFrames(_ transport: InMemoryGatewayTransport, atLeast count: Int) async -> Bool {
    await waitUntil { await transport.sentFrames().count >= count }
}

/// Hands out transports exactly like `RecordingTransportFactory`, except the transport at index
/// 1 — the attach connection `submit(_:)` sends `turn.submit` frames over — is built with
/// `sendDelay`, so a test can force `session.submitTurn(_:)`'s own `transport.send` call to
/// still be genuinely suspended while an inbound event is delivered on the same connection. This
/// is the exact ordering `SessionCoordinator.submit(_:)`'s open-before-await fix (B3, §8
/// supervisor remediation) exists to get right — see
/// `anEventArrivingWhileTheSubmitWriteIsStillInFlightFoldsIntoTheEntrySubmitAlreadyOpened`.
///
/// `attachSendFailsFromCall` optionally makes the attach transport's *n*th `send(_:)` call fail
/// after the delay (see `InMemoryGatewayTransport`'s own `failSendCallsFrom` doc comment) —
/// `nil` (default) never fails, matching every call site that only needs the reorder window, not
/// a failing write. Pass `1` to let the `attach` handshake frame (call `0`) succeed normally and
/// fail only the `turn.submit` sent afterwards over that same connection — the shape
/// `aTurnClosedAndReplacedWhileTheSubmitWriteIsInFlightThenFailingRecordsAStandaloneRefusalWithoutDisturbingTheReplacement`
/// needs.
///
/// Every other index is undecorated, matching `RecordingTransportFactory`.
private final class DelayedAttachSendTransportFactory: Sendable {
    private let state = OSAllocatedUnfairLock(initialState: [InMemoryGatewayTransport]())
    private let attachSendDelay: Duration
    private let attachSendFailsFromCall: Int?

    init(attachSendDelay: Duration, attachSendFailsFromCall: Int? = nil) {
        self.attachSendDelay = attachSendDelay
        self.attachSendFailsFromCall = attachSendFailsFromCall
    }

    var makeTransport: @Sendable () -> any GatewayTransport {
        { [self] in
            state.withLock { transports in
                let transport = transports.count == 1
                    ? InMemoryGatewayTransport(sendDelay: attachSendDelay, failSendCallsFrom: attachSendFailsFromCall)
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

/// A bare `Error` that conforms to nothing beyond `Error` itself — the
/// double `describe(_:)`'s `String(describing:)` fallback needs, since
/// every other error this suite exercises either short-circuits before
/// reaching that fallback (`.notAttached`, `createRejected`,
/// `closedByPeer` are all matched structurally) or is the
/// `CustomStringConvertible`-conforming double above, which proves the
/// *other* branch.
private struct BareUnrecognisedError: Error {}

/// A local `Error` double conforming to `CustomStringConvertible`, standing
/// in for `DeviceKeys.DeviceAuthConnectionRefused` — a real type this
/// module cannot reference (`DeviceKeys` depends on `GatewayClient`, not
/// the reverse). This is the only way to prove
/// `SessionCoordinator.describe(_:)`'s `CustomStringConvertible`-preferring
/// path from inside this module.
private struct DescribableConnectFailure: Error, CustomStringConvertible {
    let description: String
}

/// A hook double that genuinely suspends inside `SessionCoordinator.raceWindowHookForTesting`
/// — parking on a `CheckedContinuation` until `release()` is called — mirroring
/// `GatewaySessionTests.GatedTransportFactory`'s own "park for real, don't hand-time it"
/// technique (that type's own doc comment explains why forcing is preferred over reasoning),
/// applied to the different suspension point B3 review round 2 named: between
/// `SessionCoordinator.connect()`/`reattach()` reading `session.sessionId` and their own
/// `isClosed` recheck.
private final class GatedRaceWindowHook: Sendable {
    private let parkedContinuation = OSAllocatedUnfairLock<CheckedContinuation<Void, Never>?>(initialState: nil)

    /// `true` once the hook is genuinely parked awaiting `release()` — the condition a test
    /// polls for (via `waitUntil`) before racing a `close()` against the suspension.
    func hasEnteredAndIsParked() -> Bool {
        parkedContinuation.withLock { $0 != nil }
    }

    /// Resumes the parked call. A no-op if nothing is parked.
    func release() {
        let continuation = parkedContinuation.withLock { box -> CheckedContinuation<Void, Never>? in
            defer { box = nil }
            return box
        }
        continuation?.resume()
    }

    var hook: @Sendable () async -> Void {
        {
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                self.parkedContinuation.withLock { $0 = continuation }
            }
        }
    }
}

/// Exercises the package half of tasks 8.1/8.2/8.3: `SessionCoordinator`
/// drives a `GatewaySession`'s inbound stream into a `TurnTranscript` and
/// publishes `SessionSnapshot`s that never let a subscriber observe a
/// transcript out of step with the connection state alongside it.
@Suite
struct SessionCoordinatorTests {
    @Test
    func aFullTurnRendersIncrementallyAsEventsArriveOverASubstituteTransport() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        let initial = try #require(await iterator.next())
        #expect(initial.connection == .idle)
        #expect(initial.transcript.entries.isEmpty)

        let connectTask = Task { await coordinator.connect() }

        let afterConnecting = try #require(await iterator.next())
        #expect(afterConnecting.connection == .connecting)

        let createTransport = try #require(await waitForTransport(factory, at: 0))
        await createTransport.enqueue(#"{"gw":"created","sessionId":"s1"}"#)

        let attachTransport = try #require(await waitForTransport(factory, at: 1))
        await attachTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

        let afterAttached = try #require(await iterator.next())
        #expect(afterAttached.connection == .attached(sessionId: "s1"))
        await connectTask.value

        await coordinator.submit("say hello")
        let afterSubmit = try #require(await iterator.next())
        #expect(afterSubmit.transcript.entries.map(\.role) == [.user, .assistant])
        #expect(afterSubmit.transcript.entries.last?.state == .awaitingResponse)

        await attachTransport.enqueue(#"{"type":"turnStart"}"#)
        let afterStart = try #require(await iterator.next())
        #expect(afterStart.transcript.openTurn?.state == .streaming)
        #expect(afterStart.transcript.openTurn?.text == "")

        await attachTransport.enqueue(
            #"{"type":"messageDelta","message":{},"delta":{"type":"textDelta","delta":"Hel","partial":true}}"#
        )
        let afterFirstDelta = try #require(await iterator.next())
        #expect(
            afterFirstDelta.transcript.openTurn?.text == "Hel",
            "an intermediate snapshot must render partial text, not only the final reply"
        )
        #expect(afterFirstDelta.connection == .attached(sessionId: "s1"))

        await attachTransport.enqueue(
            #"{"type":"messageDelta","message":{},"delta":{"type":"textDelta","delta":"lo","partial":true}}"#
        )
        let afterSecondDelta = try #require(await iterator.next())
        #expect(afterSecondDelta.transcript.openTurn?.text == "Hello")

        await attachTransport.enqueue(#"{"type":"turnEnd","message":{},"toolResults":[]}"#)
        let afterEnd = try #require(await iterator.next())
        #expect(afterEnd.transcript.openTurn == nil)
        #expect(afterEnd.transcript.entries.last?.text == "Hello")
        #expect(afterEnd.transcript.entries.last?.state == .ended)

        await coordinator.close()
    }

    @Test
    func submittingWhileNeverConnectedRefusesAndKeepsTheTypedTextWithoutOpeningATurn() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        await coordinator.submit("hello before any connection")

        let snapshot = await coordinator.snapshot()
        #expect(snapshot.transcript.openTurn == nil)
        #expect(snapshot.transcript.entries.map(\.role) == [.user, .notice])
        #expect(snapshot.transcript.entries.first?.text == "hello before any connection")
        guard case .refused(let reason) = snapshot.transcript.entries.last?.state else {
            Issue.record("expected a refused notice entry")
            return
        }
        #expect(reason == "no session is attached")
        #expect(factory.count() == 0)
    }

    /// Finding 1 (§8 supervisor remediation): the double-submit gate `submit(_:)` itself
    /// enforces, proved the strongest way available — driving two real submits, not merely
    /// asserting `openTurn == nil` would have refused. The second submit must not disturb the
    /// first turn's open entry in any way: its id survives, and every one of its later events
    /// (`turnStart`, a delta, `turnEnd`) still folds into it, never into the refusal notice.
    @Test
    func aSecondSubmitWhileATurnIsStillOpenRefusesWithoutDisturbingTheFirst() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        _ = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)

        await coordinator.submit("a")
        let afterFirstSubmit = try #require(await iterator.next())
        #expect(afterFirstSubmit.transcript.entries.map(\.role) == [.user, .assistant])
        let firstEntryID = try #require(afterFirstSubmit.transcript.openTurn?.id)

        await coordinator.submit("b")
        let afterSecondSubmit = try #require(await iterator.next())
        #expect(afterSecondSubmit.transcript.entries.map(\.role) == [.user, .assistant, .user, .notice])
        #expect(
            afterSecondSubmit.transcript.openTurn?.id == firstEntryID,
            "the refused second submit must not disturb the first turn's open entry"
        )
        guard case .refused(let reason) = afterSecondSubmit.transcript.entries.last?.state else {
            Issue.record("expected a refused notice entry for the second submit")
            return
        }
        #expect(reason == "a turn is still open — wait for it to finish, or abandon it")
        #expect(factory.count() == 2, "a refused submit must never open a connection")

        let attachTransport = try #require(factory.transport(at: 1))
        await attachTransport.enqueue(#"{"type":"turnStart"}"#)
        let afterStart = try #require(await iterator.next())
        #expect(afterStart.transcript.openTurn?.id == firstEntryID)
        #expect(afterStart.transcript.openTurn?.state == .streaming)

        await attachTransport.enqueue(
            #"{"type":"messageDelta","message":{},"delta":{"type":"textDelta","delta":"hi","partial":true}}"#
        )
        let afterDelta = try #require(await iterator.next())
        #expect(afterDelta.transcript.entries.first { $0.id == firstEntryID }?.text == "hi")

        await attachTransport.enqueue(#"{"type":"turnEnd","message":{},"toolResults":[]}"#)
        let afterEnd = try #require(await iterator.next())
        let firstEntry = afterEnd.transcript.entries.first { $0.id == firstEntryID }
        #expect(firstEntry?.state == .ended)
        #expect(firstEntry?.text == "hi")
        #expect(afterEnd.transcript.openTurn == nil)

        await coordinator.close()
    }

    /// `abandonOpenTurn()` releases `submit(_:)`'s gate: a message typed after abandoning must
    /// open a genuinely new turn, not be refused as if the old one were still open.
    @Test
    func abandonOpenTurnAllowsANewSubmitToOpenAFreshTurn() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        _ = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)

        await coordinator.submit("a")
        let afterFirstSubmit = try #require(await iterator.next())
        let firstEntryID = try #require(afterFirstSubmit.transcript.openTurn?.id)

        await coordinator.abandonOpenTurn()
        let afterAbandon = try #require(await iterator.next())
        #expect(afterAbandon.transcript.openTurn == nil)
        #expect(afterAbandon.transcript.entries.first { $0.id == firstEntryID }?.state == .abandoned)

        await coordinator.submit("b")
        let afterSecondSubmit = try #require(await iterator.next())
        #expect(afterSecondSubmit.transcript.entries.map(\.role) == [.user, .assistant, .user, .assistant])
        let secondEntryID = try #require(afterSecondSubmit.transcript.openTurn?.id)
        #expect(secondEntryID != firstEntryID)

        await coordinator.close()
    }

    /// `abandonOpenTurn()` with nothing open must not publish a spurious change or otherwise
    /// disturb the snapshot — mirrors `TurnTranscriptTests.abandonOpenTurnWithNoOpenTurnIsA
    /// HarmlessNoOp` at the coordinator layer.
    @Test
    func abandonOpenTurnWithNoOpenTurnOnTheCoordinatorIsAHarmlessNoOp() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        await coordinator.abandonOpenTurn()

        let snapshot = await coordinator.snapshot()
        #expect(snapshot.transcript.entries.isEmpty)
        #expect(snapshot.connection == .idle)
    }

    /// Finding 1's smaller race window, forced rather than reasoned about: `session.submitTurn
    /// (_:)`'s own write can still be suspended inside `transport.send` (an artificial
    /// `sendDelay`, mirroring the technique `GatewaySessionTests` already uses for the same
    /// purpose) when an inbound event for the very turn being submitted is delivered on
    /// `consume(_:)` — a different, concurrently-scheduled task on this same actor. Before B3's
    /// fix, that event had no open turn to fold into yet (`recordSubmittedTurn(_:)` had not run)
    /// and was rendered as a fresh, orphaned entry that the eventual `recordSubmittedTurn(_:)`
    /// call then silently displaced. Opening the turn *before* awaiting the write closes that
    /// window: the entry already exists, and is already `openTurn`, the instant `submit(_:)`'s
    /// own synchronous prelude finishes — before anything else can run on this actor at all.
    @Test
    func anEventArrivingWhileTheSubmitWriteIsStillInFlightFoldsIntoTheEntrySubmitAlreadyOpened() async throws {
        let factory = DelayedAttachSendTransportFactory(attachSendDelay: .milliseconds(200))
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        // Kept alongside `iterator`, not only the iterator itself: `waitForSnapshot` takes the
        // stream (see its own doc comment for why the iterator can't cross into the detached task
        // it races internally). Every direct `iterator.next()` call below still shares the exact
        // same underlying buffer and subscription as `waitForSnapshot`'s own reads.
        let updates = await coordinator.updates()
        var iterator = updates.makeAsyncIterator()
        _ = await iterator.next()

        let connectTask = Task { await coordinator.connect() }
        _ = try #require(await iterator.next())

        let created = await waitUntil { factory.count() > 0 }
        try #require(created)
        let createTransport = try #require(factory.transport(at: 0))
        await createTransport.enqueue(#"{"gw":"created","sessionId":"s1"}"#)

        let attached = await waitUntil { factory.count() > 1 }
        try #require(attached)
        let attachTransport = try #require(factory.transport(at: 1))
        await attachTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

        _ = try #require(await iterator.next())
        await connectTask.value

        // The `attach` control frame itself already went out over this same transport, delayed
        // by the same `sendDelay` — captured here, not assumed to be `0`, so the check below
        // proves "no *new* frame yet" rather than a wrong "no frame at all".
        let sentCountAfterAttach = await attachTransport.sentFrames().count

        let submitTask = Task { await coordinator.submit("say hello") }

        // Proved, not assumed: the write must still be genuinely in flight — sleeping inside
        // `attachTransport`'s artificial `sendDelay` — when the event below is enqueued, or this
        // test would not be forcing the window it exists to force.
        try await Task.sleep(for: .milliseconds(20))
        #expect(
            await attachTransport.sentFrames().count == sentCountAfterAttach,
            "the turn.submit write must still be in flight"
        )

        await attachTransport.enqueue(#"{"type":"turnStart"}"#)

        // Not "the next snapshot the stream yields" — that assumes nothing else can be published
        // into that exact slot while `submitTask`'s own write is still genuinely in flight, which
        // is precisely the assumption this test's forced race is designed to violate. Waiting
        // until a snapshot actually reaches `.streaming` is what makes this robust to an unrelated
        // snapshot (e.g. from the write's own eventual completion) landing first under load.
        let afterStart = try #require(
            await waitForSnapshot(
                from: updates,
                description: "the submitted turn to reach .streaming after turnStart"
            ) { $0.transcript.openTurn?.state == .streaming }
        )
        #expect(
            afterStart.transcript.entries.map(\.role) == [.user, .assistant],
            "the event must fold into the entry submit(_:) already opened, not a second, orphaned one"
        )
        #expect(afterStart.transcript.openTurn?.state == .streaming)

        await submitTask.value
        let final = await coordinator.snapshot()
        #expect(final.transcript.entries.count == 2, "the write completing afterwards must not append a further entry")

        await coordinator.close()
    }

    /// The §8 supervisor's second remediation finding, forced through the real actor — not just
    /// the pure reducer (`TurnTranscriptTests.convertOpenTurnToRefusalOnAnIdThatWasReplacedByA
    /// DifferentOpenTurnLeavesTheReplacementUntouched` already covers that half deterministically
    /// and without any transport at all). Here, `submit(_:)`'s own write is genuinely suspended
    /// (`attachSendFailsFromCall: 1` — the `attach` handshake frame, call `0`, succeeds; the
    /// `turn.submit` sent afterwards, call `1`, fails only once its own `sendDelay` elapses),
    /// while — during that exact suspension — a real event sequence closes the turn `submit(_:)`
    /// opened and a further event replaces it with a different one, all delivered by `consume(_:)`
    /// running concurrently on the same actor. When the write's failure is finally discovered,
    /// `convertOpenTurnToRefusal(_:reason:)` must record a standalone refusal without disturbing
    /// the replacement — the unscoped shape this whole block replaced would have overwritten it.
    @Test
    func aTurnClosedAndReplacedWhileTheSubmitWriteIsInFlightThenFailingRecordsAStandaloneRefusalWithoutDisturbingTheReplacement() async throws {
        let factory = DelayedAttachSendTransportFactory(attachSendDelay: .milliseconds(200), attachSendFailsFromCall: 1)
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        let connectTask = Task { await coordinator.connect() }
        _ = try #require(await iterator.next())

        let created = await waitUntil { factory.count() > 0 }
        try #require(created)
        let createTransport = try #require(factory.transport(at: 0))
        await createTransport.enqueue(#"{"gw":"created","sessionId":"s1"}"#)

        let attached = await waitUntil { factory.count() > 1 }
        try #require(attached)
        let attachTransport = try #require(factory.transport(at: 1))
        await attachTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

        _ = try #require(await iterator.next())
        await connectTask.value

        let sentCountAfterAttach = await attachTransport.sentFrames().count
        let submitTask = Task { await coordinator.submit("say hello") }

        // Proved, not assumed — same discipline as the sibling test above: the `turn.submit`
        // write must still be genuinely in flight when the events below arrive.
        try await Task.sleep(for: .milliseconds(20))
        #expect(await attachTransport.sentFrames().count == sentCountAfterAttach, "the turn.submit write must still be in flight")

        // Close the turn `submit(_:)` opened, entirely, with real streamed content —
        // `turnStart` → a delta → `turnEnd` — then replace it with a *different* open turn via a
        // further delta that arrives with nothing open to fold into (`apply(_:)`'s documented
        // no-open-turn synthesis branch). All of this lands on the actor while the write above is
        // still sleeping inside its artificial `sendDelay`.
        await attachTransport.enqueue(#"{"type":"turnStart"}"#)
        await attachTransport.enqueue(
            #"{"type":"messageDelta","message":{},"delta":{"type":"textDelta","delta":"real reply","partial":true}}"#
        )
        await attachTransport.enqueue(#"{"type":"turnEnd","message":{},"toolResults":[]}"#)
        await attachTransport.enqueue(
            #"{"type":"messageDelta","message":{},"delta":{"type":"textDelta","delta":"unrelated replayed content","partial":true}}"#
        )

        // `timeout: 8`, not the local default of 2: this poll is a hang guard on real actor
        // work (four sequential enqueued frames plus the write's own 200ms `sendDelay`), not a
        // discriminator on how fast that work should complete — the observed CI flake at this
        // exact `#require` was the 2s default timing out under runner load, not a wrong-state bug.
        let replacementAppeared = await waitUntil(timeout: 8) { await coordinator.snapshot().transcript.entries.count == 3 }
        try #require(replacementAppeared, "the closed-and-replaced sequence must have landed before the write fails")

        let beforeFailure = await coordinator.snapshot()
        let firstEntryID = try #require(beforeFailure.transcript.entries.dropFirst().first?.id)
        let replacementID = try #require(beforeFailure.transcript.openTurn?.id)
        #expect(replacementID != firstEntryID)
        #expect(beforeFailure.transcript.entries.first { $0.id == firstEntryID }?.state == .ended)
        #expect(beforeFailure.transcript.entries.first { $0.id == firstEntryID }?.text == "real reply")

        // Now let the write's `sendDelay` elapse and fail — `convertOpenTurnToRefusal(_:reason:)`
        // is called with `firstEntryID`, which is no longer `openTurnID` at all.
        await submitTask.value

        let final = await coordinator.snapshot()
        let firstEntry = try #require(final.transcript.entries.first { $0.id == firstEntryID })
        #expect(firstEntry.state == .ended, "the original entry's real, delivered content must survive untouched")
        #expect(firstEntry.text == "real reply")

        let replacementEntry = try #require(final.transcript.entries.first { $0.id == replacementID })
        #expect(
            replacementEntry.role == .assistant,
            "the replacement entry must not be overwritten by a refusal for a different, already-failed write"
        )
        #expect(replacementEntry.text == "unrelated replayed content")
        #expect(replacementEntry.state == .streaming)
        #expect(final.transcript.openTurn?.id == replacementID, "the genuinely open turn must stay open")

        #expect(final.transcript.entries.count == 4, "a standalone refusal notice must be appended, not silently dropped")
        let notice = try #require(final.transcript.entries.last)
        #expect(notice.role == .notice)
        let expectedReason = String(describing: GatewayTransportError.closedLocally)
        #expect(notice.text == expectedReason)
        #expect(notice.state == .refused(reason: expectedReason))

        await coordinator.close()
    }

    @Test
    func aCreateRejectedReplyClassifiesAsCreateRejectedNotOther() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        let connectTask = Task { await coordinator.connect() }
        _ = await iterator.next()

        let createTransport = try #require(await waitForTransport(factory, at: 0))
        await createTransport.enqueue(
            #"{"gw":"createRejected","code":"cap_reached","message":"too many sessions"}"#
        )

        let afterRejection = try #require(await iterator.next())
        #expect(afterRejection.connection == .connectFailed(.createRejected(code: "cap_reached", message: "too many sessions")))
        await connectTask.value
    }

    @Test
    func aPeerCloseSupersededByANewerAttachSurvivesWithItsCodeIntact() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        let (attachTransport, _) = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)

        await attachTransport.simulateClose(code: .supersededByNewerAttach, reason: "a newer attach took over")
        let afterDrop = try #require(await iterator.next())
        #expect(afterDrop.connection == .dropped(.closedByPeer(code: .supersededByNewerAttach, reason: "a newer attach took over")))
    }

    @Test
    func aPeerCloseForCoreFailureSurvivesWithItsCodeIntact() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        let (attachTransport, _) = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)

        await attachTransport.simulateClose(code: .coreFailure, reason: "the core crashed")
        let afterDrop = try #require(await iterator.next())
        #expect(afterDrop.connection == .dropped(.closedByPeer(code: .coreFailure, reason: "the core crashed")))
    }

    @Test
    func aSecondConnectCallIsANoOpAndOpensNoSecondConnection() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        _ = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)
        #expect(factory.count() == 2)

        await coordinator.connect()
        #expect(factory.count() == 2, "an already-attached coordinator must not open a second connection")
        #expect(await coordinator.snapshot().connection == .attached(sessionId: "s1"))
    }

    @Test
    func reattachFromDroppedResubscribesAndRendersEventsOnTheNewStream() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        _ = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)

        let firstAttachTransport = try #require(factory.transport(at: 1))
        await firstAttachTransport.simulateClose(code: .normal, reason: "went away")
        let afterDrop = try #require(await iterator.next())
        #expect(afterDrop.connection == .dropped(.closedByPeer(code: .normal, reason: "went away")))

        let reattachTask = Task { await coordinator.reattach() }
        let afterConnecting = try #require(await iterator.next())
        #expect(afterConnecting.connection == .connecting)

        let secondAttachTransport = try #require(await waitForTransport(factory, at: 2))
        await secondAttachTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":0,"wire":"0.2"}"#)

        let afterReattached = try #require(await iterator.next())
        #expect(afterReattached.connection == .attached(sessionId: "s1"))
        await reattachTask.value

        // The old stream is finished; only the new one can still deliver
        // events. A frame enqueued on the superseded transport must never
        // reach this coordinator.
        await firstAttachTransport.enqueue(#"{"type":"turnStart"}"#)
        await secondAttachTransport.enqueue(#"{"type":"turnStart"}"#)

        let afterEvent = try #require(await iterator.next())
        #expect(afterEvent.transcript.entries.count == 1)
        #expect(afterEvent.transcript.openTurn?.state == .streaming)

        await coordinator.close()
    }

    /// Blocker 1 (review round 1): a `reattach()` that itself fails must
    /// not permanently strand the session in a state only `connect()` (a
    /// brand-new session, discarding this one's replay cursor) can escape.
    /// `GatewaySession.performAttach`'s own doc calls a handshake failing
    /// mid-reattach "the ordinary transient case `reattach()` exists to
    /// recover from" — so a second `reattach()` from `.connectFailed` must
    /// be a genuine retry, not a no-op.
    @Test
    func reattachRetriesAfterAFailedReattachRatherThanBeingPermanentlyLockedOut() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        _ = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)

        let firstAttachTransport = try #require(factory.transport(at: 1))
        await firstAttachTransport.simulateClose(code: .normal, reason: "went away")
        _ = try #require(await iterator.next())

        // First reattach: the connection ends before `attached` ever
        // arrives — the same "close the raw transport directly" shape
        // `GatewaySessionTests.theConnectionEndingBeforeAttachedThrowsADistinctError`
        // uses to force `GatewaySessionError.connectionClosedBeforeAttached`.
        let firstRetryTask = Task { await coordinator.reattach() }
        _ = try #require(await iterator.next())

        let failedAttachTransport = try #require(await waitForTransport(factory, at: 2))
        let sent = await waitForSentFrames(failedAttachTransport, atLeast: 1)
        #expect(sent)
        await failedAttachTransport.close()

        let afterFailedReattach = try #require(await iterator.next())
        guard case .connectFailed = afterFailedReattach.connection else {
            Issue.record("expected .connectFailed after the reattach handshake failed, got \(afterFailedReattach.connection)")
            return
        }
        await firstRetryTask.value

        // Second reattach, from `.connectFailed`: must be a genuine retry
        // — a new connection, not a silent no-op. Proved below via a
        // bounded poll (`waitUntil`, which cannot hang — it returns
        // `false` past its deadline) on facts that can't lie about a
        // no-op: the factory opened a new transport, and the connection
        // state moved off `.connectFailed`. This runs *before* the
        // `await iterator.next()` calls that follow, deliberately: if the
        // guard this test exists to protect (`reattach()`'s `.dropped`
        // check) regresses back to `.connectFailed`-excluding, the second
        // `reattach()` above becomes a no-op, `coordinator.updates()` never
        // emits again, and `await iterator.next()` would hang rather than
        // fail — `.timeLimit` cannot bound that hang (it cancels
        // cooperatively; nothing here is cancellation-aware), see
        // tech-debt/swift-testing-timelimit-does-not-bound-continuation-hangs.md.
        // `try #require` (not `#expect`) on the bounded poll below is
        // deliberate too: `#require` stops the test on failure, so a
        // regression fails here in well under 2s instead of merely
        // recording an issue and then wedging on the `iterator.next()`
        // below anyway.
        //
        // Verified by hand, not just reasoned: temporarily narrowing the
        // `reattach()` guard above back to `.dropped`-only (the exact
        // pre-fix shape) and running this test alone made it fail at the
        // `secondConnectionOpened` requirement in 2.018s, not hang — the
        // guard change was reverted immediately after (`git diff
        // home/Sources/` confirmed empty) and the full suite re-run green
        // at 372/43 before this comment was written. That same pass also
        // caught a real bug in an earlier draft of this check: comparing
        // `factory.count()` against the literal `2` rather than its value
        // just before this second `reattach()` call, which was already 3
        // by this point (create + first attach + the first, failed,
        // reattach) — so the check was vacuously true and the regression
        // was only caught by `movedOffConnectFailed`. `countBeforeSecondReattach`
        // below fixes that; forcing the regression is what surfaced it.
        let countBeforeSecondReattach = factory.count()
        let secondRetryTask = Task { await coordinator.reattach() }

        let secondConnectionOpened = await waitUntil { factory.count() > countBeforeSecondReattach }
        try #require(secondConnectionOpened, "reattach() from .connectFailed must open a new connection, not no-op")

        let movedOffConnectFailed = await waitUntil { await coordinator.snapshot().connection != afterFailedReattach.connection }
        try #require(movedOffConnectFailed, "reattach() from .connectFailed must change the connection state, not leave it stuck")

        let afterConnecting = try #require(await iterator.next())
        #expect(afterConnecting.connection == .connecting)

        let secondAttachTransport = try #require(await waitForTransport(factory, at: 3))
        await secondAttachTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":0,"wire":"0.2"}"#)

        let afterReattached = try #require(await iterator.next())
        #expect(afterReattached.connection == .attached(sessionId: "s1"))
        await secondRetryTask.value

        #expect(factory.count() == 4)
        await coordinator.close()
    }

    @Test
    func reattachFromIdleIsANoOp() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        await coordinator.reattach()

        #expect(await coordinator.snapshot().connection == .idle)
        #expect(factory.count() == 0)
    }

    @Test
    func reattachFromAttachedIsANoOp() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        _ = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)

        await coordinator.reattach()

        #expect(await coordinator.snapshot().connection == .attached(sessionId: "s1"))
        #expect(factory.count() == 2, "a reattach attempted while already attached must not open a connection")
    }

    @Test
    func updatesYieldsTheCurrentSnapshotImmediatelyThenOnePerChangeAndUnregistersOnTermination() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        do {
            let stream = await coordinator.updates()
            var iterator = stream.makeAsyncIterator()
            let initial = try #require(await iterator.next())
            #expect(initial.connection == .idle)
            #expect(await coordinator.subscriberCountForTesting == 1)

            await coordinator.submit("hello")
            let afterSubmit = try #require(await iterator.next())
            #expect(afterSubmit.transcript.entries.map(\.role) == [.user, .notice])
        }

        let unregistered = await waitUntil { await coordinator.subscriberCountForTesting == 0 }
        #expect(unregistered)
    }

    /// Blocker 4 (review round 1): `isClosing` exists solely to guarantee
    /// `close()` ends in `.dropped(.closedLocally)` rather than losing that
    /// race to `consume(_:)`'s own "stream ended" branch publishing
    /// `.streamEnded` over the top of it — a mechanism that delicate needs
    /// its outcome actually asserted, not just its reasoning documented.
    @Test
    func closePublishesDroppedClosedLocally() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        _ = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)

        await coordinator.close()

        #expect(await coordinator.snapshot().connection == .dropped(.closedLocally))
    }

    /// B3 review round 1, blocker 2: `close()` is terminal — nothing calls `connect()` or
    /// `reattach()` on this coordinator again after it, so a call that reaches this coordinator
    /// once `close()` has already run must not open a connection. Asserted the strongest way
    /// available, the same discipline `submittingWhileNeverConnectedRefusesAndKeepsTheTypedTextWithoutOpeningATurn`
    /// and `aSecondConnectCallIsANoOpAndOpensNoSecondConnection` already use: against the
    /// factory's own transport count, not merely the published connection state.
    @Test
    func connectAfterCloseIsANoOpAndOpensNoConnection() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        _ = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)
        #expect(factory.count() == 2)

        await coordinator.close()
        let countAfterClose = factory.count()

        await coordinator.connect()

        #expect(factory.count() == countAfterClose, "connect() after close() must not open a connection")
        #expect(await coordinator.snapshot().connection == .dropped(.closedLocally), "close()'s own outcome must survive a later connect()")
    }

    /// Same guarantee as `connectAfterCloseIsANoOpAndOpensNoConnection`, for `reattach()` —
    /// the sibling entry point `close()` must lock out identically.
    @Test
    func reattachAfterCloseIsANoOpAndOpensNoConnection() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        _ = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)
        #expect(factory.count() == 2)

        await coordinator.close()
        let countAfterClose = factory.count()

        await coordinator.reattach()

        #expect(factory.count() == countAfterClose, "reattach() after close() must not open a connection")
        #expect(await coordinator.snapshot().connection == .dropped(.closedLocally), "close()'s own outcome must survive a later reattach()")
    }

    /// B3 review round 2: the one blocker left after round 1's `isClosed` fix. `connect()`'s
    /// entry check happens before its handshake even starts, but `await session.sessionId` —
    /// a genuine cross-actor suspension (`GatewaySession.sessionId` is `public private(set)`,
    /// not `nonisolated`) — used to sit *after* the only recheck, unguarded. The reviewer
    /// forced a `close()` into exactly that window by temporarily instrumenting `connect()`
    /// and reverting; this test makes that forcing permanent via `setRaceWindowHookForTesting`,
    /// parking `connect()` at the real suspension point the fix now rechecks after, and racing
    /// a `close()` into it for real, rather than trusting the reasoning that the window is
    /// closed.
    @Test
    func closeWinsAConnectThatRacesPastTheEntryCheckIntoThePostHandshakeWindow() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)
        let gate = GatedRaceWindowHook()
        await coordinator.setRaceWindowHookForTesting(gate.hook)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        let connectTask = Task { await coordinator.connect() }
        _ = try #require(await iterator.next())

        let createTransport = try #require(await waitForTransport(factory, at: 0))
        await createTransport.enqueue(#"{"gw":"created","sessionId":"s1"}"#)
        let attachTransport = try #require(await waitForTransport(factory, at: 1))
        await attachTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

        // `connect()` has now completed the handshake, read `sessionId`, and is genuinely
        // parked in the hook — past its entry check, inside the window round 2 found
        // unguarded. Proved, not assumed: a bounded poll, not a fixed sleep.
        let parked = await waitUntil { gate.hasEnteredAndIsParked() }
        try #require(parked, "connect() must have reached the post-handshake hook before this test can race it")

        await coordinator.close()
        #expect(await coordinator.snapshot().connection == .dropped(.closedLocally))

        gate.release()
        await connectTask.value

        #expect(
            await coordinator.snapshot().connection == .dropped(.closedLocally),
            "close() must win a connect() that raced past the entry check into the post-handshake window"
        )
    }

    /// Same window as `closeWinsAConnectThatRacesPastTheEntryCheckIntoThePostHandshakeWindow`,
    /// for `reattach()` — the sibling method must not diverge in this shape either.
    @Test
    func closeWinsAReattachThatRacesPastTheEntryCheckIntoThePostHandshakeWindow() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        var iterator = await coordinator.updates().makeAsyncIterator()
        _ = await iterator.next()

        _ = try await connectCoordinator(coordinator, factory: factory, iterator: &iterator)
        let firstAttachTransport = try #require(factory.transport(at: 1))
        await firstAttachTransport.simulateClose(code: .normal, reason: "went away")
        _ = try #require(await iterator.next())

        let gate = GatedRaceWindowHook()
        await coordinator.setRaceWindowHookForTesting(gate.hook)

        let reattachTask = Task { await coordinator.reattach() }
        _ = try #require(await iterator.next())

        let secondAttachTransport = try #require(await waitForTransport(factory, at: 2))
        await secondAttachTransport.enqueue(#"{"gw":"attached","generation":2,"headSeq":0,"wire":"0.2"}"#)

        let parked = await waitUntil { gate.hasEnteredAndIsParked() }
        try #require(parked, "reattach() must have reached the post-handshake hook before this test can race it")

        await coordinator.close()
        #expect(await coordinator.snapshot().connection == .dropped(.closedLocally))

        gate.release()
        await reattachTask.value

        #expect(
            await coordinator.snapshot().connection == .dropped(.closedLocally),
            "close() must win a reattach() that raced past the entry check into the post-handshake window"
        )
    }

    @Test
    func anUnrecognisedErrorConformingToCustomStringConvertibleRendersItsDescription() async throws {
        struct FailingFactory {
            let makeTransport: @Sendable () async throws -> any GatewayTransport = {
                throw DescribableConnectFailure(description: "device key refused: unknown device")
            }
        }

        let factory = FailingFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        await coordinator.connect()

        let snapshot = await coordinator.snapshot()
        #expect(snapshot.connection == .connectFailed(.other(message: "device key refused: unknown device")))
    }

    /// Blocker 3 (review round 1): the test above only proves the
    /// `CustomStringConvertible`-conforming half of `describe(_:)`. Neither
    /// `GatewaySessionError` nor `GatewayTransportError` conforms to
    /// `CustomStringConvertible`, so this is what actually forces a
    /// non-conforming error through the `String(describing:)` fallback and
    /// proves that path renders something real — not empty, not a crash —
    /// rather than trusting the `Any`-cast reasoning on its own.
    @Test
    func anUnrecognisedNonConformingErrorFallsBackToStringDescribing() async throws {
        struct FailingFactory {
            let makeTransport: @Sendable () async throws -> any GatewayTransport = {
                throw BareUnrecognisedError()
            }
        }

        let factory = FailingFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)
        let coordinator = SessionCoordinator(session: session)

        await coordinator.connect()

        let expected = String(describing: BareUnrecognisedError())
        #expect(!expected.isEmpty)

        let snapshot = await coordinator.snapshot()
        #expect(snapshot.connection == .connectFailed(.other(message: expected)))
    }

    /// Runs a full connect handshake (`s1`/generation 1) against `factory`,
    /// draining `iterator` through `.connecting` and `.attached`. Returns
    /// the attach transport (index 1) so a test can drive events or a
    /// simulated close on it.
    private func connectCoordinator(
        _ coordinator: SessionCoordinator,
        factory: RecordingTransportFactory,
        iterator: inout AsyncStream<SessionSnapshot>.Iterator
    ) async throws -> (attachTransport: InMemoryGatewayTransport, createTransport: InMemoryGatewayTransport) {
        let connectTask = Task { await coordinator.connect() }
        _ = try #require(await iterator.next())

        let createTransport = try #require(await waitForTransport(factory, at: 0))
        await createTransport.enqueue(#"{"gw":"created","sessionId":"s1"}"#)

        let attachTransport = try #require(await waitForTransport(factory, at: 1))
        await attachTransport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

        _ = try #require(await iterator.next())
        await connectTask.value

        return (attachTransport, createTransport)
    }
}

/// `GatewayConnectionState.allowsConnect`/`allowsReattach` are the single source of truth
/// `SessionCoordinator.connect()`/`reattach()` guard on themselves (see those methods' own doc
/// comments) — exercised exhaustively here, over every case, so a case added or reclassified
/// later has a test that must be updated alongside it rather than a silently stale assumption.
/// `SessionCoordinatorTests` above keeps the complementary behavioural proof: that a call from an
/// illegal state is actually a no-op, not merely that the property says it should be.
@Suite
struct GatewayConnectionStateLegalityTests {
    @Test
    func allowsConnectIsTrueOnlyForIdleConnectFailedAndDropped() {
        #expect(GatewayConnectionState.idle.allowsConnect)
        #expect(!GatewayConnectionState.connecting.allowsConnect)
        #expect(!GatewayConnectionState.attached(sessionId: "s1").allowsConnect)
        #expect(GatewayConnectionState.dropped(.closedLocally).allowsConnect)
        #expect(GatewayConnectionState.connectFailed(.other(message: "boom")).allowsConnect)
    }

    @Test
    func allowsReattachIsTrueOnlyForDroppedAndConnectFailed() {
        #expect(!GatewayConnectionState.idle.allowsReattach)
        #expect(!GatewayConnectionState.connecting.allowsReattach)
        #expect(!GatewayConnectionState.attached(sessionId: "s1").allowsReattach)
        #expect(GatewayConnectionState.dropped(.closedLocally).allowsReattach)
        #expect(GatewayConnectionState.connectFailed(.other(message: "boom")).allowsReattach)
    }
}
