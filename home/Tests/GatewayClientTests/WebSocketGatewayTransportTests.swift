import Foundation
import Testing
@testable import GatewayClient

/// `WebSocketGatewayTransport` itself makes one `URLSession` call per
/// method — deliberately, per the block brief, so nothing beyond that call
/// is untested. What *is* logic is `closedError(closeCode:reasonData:)`,
/// factored out as a pure static function precisely so it can be tested
/// here without a live socket.
@Suite
struct WebSocketGatewayTransportTests {
    @Test
    func aZeroCloseCodeIsNotTreatedAsAPeerInitiatedClose() {
        let error = WebSocketGatewayTransport.closedError(closeCode: 0, reasonData: nil)
        #expect(error == nil)
    }

    @Test
    func aNonZeroCloseCodeWithNoReasonMapsToAnEmptyReason() {
        let error = WebSocketGatewayTransport.closedError(closeCode: 4404, reasonData: nil)
        #expect(error == .closed(code: .unknownSession, reason: ""))
    }

    @Test
    func aReasonPayloadIsDecodedAsUTF8() {
        let reasonData = "session 'abc' not found".data(using: .utf8)
        let error = WebSocketGatewayTransport.closedError(closeCode: 4404, reasonData: reasonData)
        #expect(error == .closed(code: .unknownSession, reason: "session 'abc' not found"))
    }

    @Test
    func anUnrecognisedCloseCodeIsPreservedAsOther() {
        let error = WebSocketGatewayTransport.closedError(closeCode: 4999, reasonData: nil)
        #expect(error == .closed(code: .other(4999), reason: ""))
    }

    @Test
    func headersAreAssembledIntoTheRequestWithoutBakingInLoopback() {
        let endpoint = GatewayEndpoint(
            url: URL(string: "wss://home.example.ts.net/ws")!,
            headers: ["Authorization": "Bearer token-value"]
        )

        let request = WebSocketGatewayTransport.request(for: endpoint)

        #expect(request.url == endpoint.url)
        #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer token-value")
    }

    @Test
    func theDefaultEndpointProducesARequestWithNoHeaders() {
        let request = WebSocketGatewayTransport.request(for: GatewayEndpoint())

        #expect(request.url == GatewayEndpoint.defaultURL)
        #expect(request.allHTTPHeaderFields?.isEmpty ?? true)
    }

    // MARK: - Lifecycle parity with InMemoryGatewayTransport
    //
    // `URLSessionWebSocketTask.resume()` starts the handshake in the
    // background without blocking and without requiring a reachable
    // server — these transitions never await network I/O, so they need
    // neither a live gateway nor a fake one. The fourth lifecycle fact,
    // a peer-initiated close carrying a real code and reason, cannot be
    // reached offline: it depends on `URLSessionWebSocketTask.closeCode`
    // being populated by an actual close handshake, which is exactly the
    // one `URLSession` behaviour this conformer is deliberately too thin
    // to have logic of its own to test (see `closedError` above, tested
    // as a pure function instead).

    @Test
    func sendBeforeConnectFailsWithNotConnected() async throws {
        let transport = WebSocketGatewayTransport()
        await #expect(throws: GatewayTransportError.notConnected) {
            try await transport.send("too early")
        }
    }

    @Test
    func receiveBeforeConnectFailsWithNotConnected() async throws {
        let transport = WebSocketGatewayTransport()
        await #expect(throws: GatewayTransportError.notConnected) {
            try await transport.receive()
        }
    }

    @Test
    func sendAfterALocalCloseFailsWithClosedLocallyNotNotConnected() async throws {
        let transport = WebSocketGatewayTransport()
        try await transport.connect()
        await transport.close()

        await #expect(throws: GatewayTransportError.closedLocally) {
            try await transport.send("late frame")
        }
    }

    @Test
    func receiveAfterALocalCloseFailsWithClosedLocallyNotNotConnected() async throws {
        let transport = WebSocketGatewayTransport()
        try await transport.connect()
        await transport.close()

        await #expect(throws: GatewayTransportError.closedLocally) {
            try await transport.receive()
        }
    }

    @Test
    func closingATransportThatWasNeverConnectedLeavesItNotConnected() async throws {
        let transport = WebSocketGatewayTransport()
        await transport.close()

        await #expect(throws: GatewayTransportError.notConnected) {
            try await transport.send("still never connected")
        }
    }

    @Test
    func closeIsIdempotentAfterConnecting() async throws {
        let transport = WebSocketGatewayTransport()
        try await transport.connect()
        await transport.close()
        await transport.close()

        await #expect(throws: GatewayTransportError.closedLocally) {
            try await transport.send("frame")
        }
    }
}
