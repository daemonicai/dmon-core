import Foundation
import Testing
@testable import GatewayClient

@Suite
struct GatewayEndpointTests {
    @Test
    func defaultURLIsDmonNetworksLoopbackBindAddress() {
        let endpoint = GatewayEndpoint()
        #expect(endpoint.url == URL(string: "ws://127.0.0.1:5500/ws")!)
    }

    @Test
    func defaultHeadersAreEmpty() {
        let endpoint = GatewayEndpoint()
        #expect(endpoint.headers.isEmpty)
    }

    /// The endpoint is configuration, not an assumption (design D2): a
    /// caller can point it at a non-loopback host without the transport
    /// changing shape.
    @Test
    func aCustomURLOverridesTheLoopbackDefault() {
        let url = URL(string: "wss://home.example.ts.net/ws")!
        let endpoint = GatewayEndpoint(url: url)
        #expect(endpoint.url == url)
    }

    /// The headers dictionary exists so a later block can carry
    /// `Authorization: Bearer …` without this type's shape changing.
    @Test
    func headersAreCarriedVerbatim() {
        let endpoint = GatewayEndpoint(
            url: GatewayEndpoint.defaultURL,
            headers: ["Authorization": "Bearer token-value"]
        )
        #expect(endpoint.headers == ["Authorization": "Bearer token-value"])
    }
}
