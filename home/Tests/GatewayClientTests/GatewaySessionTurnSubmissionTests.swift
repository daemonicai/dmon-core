import Foundation
import Testing
@testable import GatewayClient

/// Polls `condition` until it is true or `timeout` elapses. Duplicated
/// locally rather than shared, matching `GatewaySessionTests`' and
/// `GatewayConnectionTests`' own precedent for this exact helper.
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

/// Attaches `session` on a fresh transport from `factory`, replying
/// `attached` immediately, and returns the resulting stream plus the
/// attach transport — the fixture every test below that needs a live
/// attach starts from, so `GatewaySession.submitTurn(_:)` has a connection
/// to send on.
private func attachSession(
    _ session: GatewaySession,
    factory: RecordingTransportFactory
) async throws -> (stream: AsyncThrowingStream<GatewayInboundItem, Error>, transport: InMemoryGatewayTransport) {
    let handshake = Task {
        try await session.attach(sessionId: "s1", lastSeq: 0)
    }

    let transport = try #require(await waitForTransport(factory, at: 0))
    let sent = await waitForSentFrames(transport, atLeast: 1)
    #expect(sent)
    await transport.enqueue(#"{"gw":"attached","generation":1,"headSeq":0,"wire":"0.2"}"#)

    let stream = try await handshake.value
    return (stream, transport)
}

/// Exercises `GatewaySession.submitTurn(_:)` (task 7.4): it is refused
/// while not attached, sends a `turn.submit` command carrying a fresh id
/// on the attach connection, and a submit answered only with an ADR-003
/// `error` event (the `turnInProgress` case — `TurnHandler.SubmitAsync`
/// never emits `turnStart`/`turnEnd` for that case) still projects to a
/// terminal `TurnEvent` rather than leaving a consumer waiting forever.
@Suite
struct GatewaySessionTurnSubmissionTests {
    @Test
    func submitTurnIsRefusedWhileNotAttached() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        await #expect(throws: GatewaySessionError.notAttached) {
            _ = try await session.submitTurn("hello")
        }

        #expect(factory.count() == 0)
    }

    @Test
    func submitTurnIsRefusedAfterTheAttachedConnectionHasBeenClosed() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        _ = try await attachSession(session, factory: factory)
        await session.close()

        await #expect(throws: GatewaySessionError.notAttached) {
            _ = try await session.submitTurn("hello")
        }
    }

    @Test
    func submitTurnSendsATurnSubmitCommandCarryingTheReturnedId() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let (_, transport) = try await attachSession(session, factory: factory)

        let id = try await session.submitTurn("hello there")

        let sentAfterAttach = await waitForSentFrames(transport, atLeast: 2)
        #expect(sentAfterAttach)
        let sent = await transport.sentFrames()
        #expect(sent.last == #"{"id":"\#(id)","message":"hello there","type":"turn.submit"}"#)

        await session.close()
    }

    /// Two submits on the same session must never carry the same id — the
    /// host dedups a `turn.submit` on exact id equality, so a reused id
    /// would make the second submit look like a retry of the first.
    @Test
    func twoSubmitsOnTheSameSessionCarryDifferentIds() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        _ = try await attachSession(session, factory: factory)

        let firstId = try await session.submitTurn("first")
        let secondId = try await session.submitTurn("second")

        #expect(firstId != secondId)

        await session.close()
    }

    @Test
    func submitTurnDoesNotAdvanceTheSequenceCursor() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        _ = try await attachSession(session, factory: factory)
        let before = await session.lastObservedSeq

        _ = try await session.submitTurn("hello")

        #expect(await session.lastObservedSeq == before)

        await session.close()
    }

    /// The hazard the brief names explicitly: a submit that races a turn
    /// already in flight is answered with an ADR-003 `error` event and
    /// *no* `turnStart`/`turnEnd` at all. A renderer keyed only on
    /// `turnEnd` would wedge forever; this proves the stream still yields
    /// an item that projects to a terminal `TurnEvent`.
    @Test
    func aSubmitAnsweredOnlyWithAnErrorEventStillProjectsToATerminalTurnEvent() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let (stream, transport) = try await attachSession(session, factory: factory)

        _ = try await session.submitTurn("second turn while one is in flight")

        // No `turnStart`, no `turnEnd` — exactly what `TurnHandler
        // .SubmitAsync` emits for a concurrent submit: an `error` event and
        // nothing else from the turn lifecycle.
        await transport.enqueue(
            #"{"type":"error","code":"turnInProgress","message":"a turn is already running","recoverable":true}"#
        )

        var iterator = stream.makeAsyncIterator()
        let item = try await iterator.next()
        let turnEvent = item.flatMap(TurnProjection.project)

        #expect(turnEvent == .failed(
            code: "turnInProgress",
            message: "a turn is already running",
            recoverable: true
        ))

        await session.close()
    }

    /// A full happy-path streamed turn: `turnStart`, two `textDelta`
    /// `messageDelta` events, then `turnEnd` — each projects in order, and
    /// the turn reaches its terminal `TurnEvent` on `turnEnd`.
    @Test
    func aTurnStreamsToCompletionThroughTurnStartDeltasAndTurnEnd() async throws {
        let factory = RecordingTransportFactory()
        let session = GatewaySession(makeTransport: factory.makeTransport)

        let (stream, transport) = try await attachSession(session, factory: factory)

        _ = try await session.submitTurn("say hello")

        await transport.enqueue(#"{"type":"turnStart"}"#)
        await transport.enqueue(
            #"{"type":"messageDelta","message":{},"delta":{"type":"textDelta","delta":"Hel","partial":true}}"#
        )
        await transport.enqueue(
            #"{"type":"messageDelta","message":{},"delta":{"type":"textDelta","delta":"lo","partial":true}}"#
        )
        await transport.enqueue(#"{"type":"turnEnd","message":{},"toolResults":[]}"#)

        var iterator = stream.makeAsyncIterator()
        var rendered: [TurnEvent] = []
        for _ in 0..<4 {
            let item = try await iterator.next()
            if let turnEvent = item.flatMap(TurnProjection.project) {
                rendered.append(turnEvent)
            }
        }

        #expect(rendered == [.turnStarted, .textDelta("Hel"), .textDelta("lo"), .turnEnded])

        await session.close()
    }
}
