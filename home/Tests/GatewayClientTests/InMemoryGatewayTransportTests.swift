import Testing
@testable import GatewayClient

/// Exercises `InMemoryGatewayTransport` itself — the substrate sections 7
/// and 8 drive their handshake/turn/event tests through — proving frames
/// round-trip through the `GatewayTransport` abstraction with no network
/// connection (Requirement: "A substitute transport drives the client").
@Suite
struct InMemoryGatewayTransportTests {
    @Test
    func aFrameSentIsObservedByTheTest() async throws {
        let transport = InMemoryGatewayTransport()
        try await transport.connect()

        try await transport.send("gw:attach")

        #expect(await transport.sentFrames() == ["gw:attach"])
    }

    @Test
    func multipleFramesSentArePreservedInOrder() async throws {
        let transport = InMemoryGatewayTransport()
        try await transport.connect()

        try await transport.send("first")
        try await transport.send("second")

        #expect(await transport.sentFrames() == ["first", "second"])
    }

    @Test
    func aFrameEnqueuedBeforeReceiveIsReturned() async throws {
        let transport = InMemoryGatewayTransport()
        try await transport.connect()
        await transport.enqueue("gw:attached")

        let frame = try await transport.receive()

        #expect(frame == "gw:attached")
    }

    @Test
    func receiveSuspendsUntilAFrameIsEnqueued() async throws {
        let transport = InMemoryGatewayTransport()
        try await transport.connect()

        async let received = transport.receive()
        await transport.enqueue("gw:attached")

        #expect(try await received == "gw:attached")
    }

    @Test
    func aSimulatedCloseSurfacesItsCodeAndReasonFromReceive() async throws {
        let transport = InMemoryGatewayTransport()
        try await transport.connect()
        await transport.simulateClose(code: .unknownSession, reason: "session 'abc' not found")

        await #expect(throws: GatewayTransportError.closed(
            code: .unknownSession,
            reason: "session 'abc' not found"
        )) {
            try await transport.receive()
        }
    }

    @Test
    func aSimulatedCloseSurfacesItsCodeAndReasonFromSend() async throws {
        let transport = InMemoryGatewayTransport()
        try await transport.connect()
        await transport.simulateClose(code: .supersededByNewerAttach, reason: "superseded")

        await #expect(throws: GatewayTransportError.closed(
            code: .supersededByNewerAttach,
            reason: "superseded"
        )) {
            try await transport.send("late frame")
        }
    }

    @Test
    func aSimulatedCloseDrainsAlreadyEnqueuedFramesFirst() async throws {
        let transport = InMemoryGatewayTransport()
        try await transport.connect()
        await transport.enqueue("buffered")
        await transport.simulateClose(code: .coreFailure, reason: "session create failed")

        let frame = try await transport.receive()
        #expect(frame == "buffered")

        await #expect(throws: GatewayTransportError.closed(
            code: .coreFailure,
            reason: "session create failed"
        )) {
            try await transport.receive()
        }
    }

    @Test
    func operationsBeforeConnectFailWithNotConnected() async throws {
        let transport = InMemoryGatewayTransport()

        await #expect(throws: GatewayTransportError.notConnected) {
            try await transport.send("too early")
        }
        await #expect(throws: GatewayTransportError.notConnected) {
            try await transport.receive()
        }
    }

    @Test
    func aLocalCloseSurfacesAsClosedLocallyFromReceive() async throws {
        let transport = InMemoryGatewayTransport()
        try await transport.connect()
        await transport.close()

        await #expect(throws: GatewayTransportError.closedLocally) {
            try await transport.receive()
        }
    }

    @Test
    func aLocalCloseSurfacesAsClosedLocallyFromSend() async throws {
        let transport = InMemoryGatewayTransport()
        try await transport.connect()
        await transport.close()

        await #expect(throws: GatewayTransportError.closedLocally) {
            try await transport.send("late frame")
        }
    }

    @Test
    func aLocalCloseDrainsAlreadyEnqueuedFramesFirst() async throws {
        let transport = InMemoryGatewayTransport()
        try await transport.connect()
        await transport.enqueue("buffered")
        await transport.close()

        let frame = try await transport.receive()
        #expect(frame == "buffered")

        await #expect(throws: GatewayTransportError.closedLocally) {
            try await transport.receive()
        }
    }

    /// `close()` guards on `hasConnected` before setting `closedLocally`,
    /// so calling it on a transport that was never connected is a no-op
    /// and the transport stays `.notConnected`. Delete that guard and
    /// this breaks.
    @Test
    func closingATransportThatWasNeverConnectedLeavesItNotConnected() async throws {
        let transport = InMemoryGatewayTransport()
        await transport.close()

        await #expect(throws: GatewayTransportError.notConnected) {
            try await transport.send("still never connected")
        }
        await #expect(throws: GatewayTransportError.notConnected) {
            try await transport.receive()
        }
    }

    @Test
    func closeIsIdempotent() async throws {
        let transport = InMemoryGatewayTransport()
        try await transport.connect()
        await transport.close()
        await transport.close()

        await #expect(throws: GatewayTransportError.closedLocally) {
            try await transport.send("frame")
        }
    }
}
