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

    /// The headers dictionary carries whatever a caller assembles,
    /// verbatim.
    @Test
    func headersAreCarriedVerbatim() {
        let endpoint = GatewayEndpoint(
            url: GatewayEndpoint.defaultURL,
            headers: ["Authorization": "Bearer token-value"]
        )
        #expect(endpoint.headers == ["Authorization": "Bearer token-value"])
    }

    // MARK: - headers(for:additionalHeaders:)

    @Test
    func aCredentialProducesExactlyOneAuthorizationBearerHeader() {
        let credential = DeviceCredential(keyId: "device-1", secret: "the-secret-token")
        let headers = GatewayEndpoint.headers(for: credential)
        #expect(headers == ["Authorization": "Bearer the-secret-token"])
    }

    /// Not an empty or placeholder value — the key itself must be absent.
    @Test
    func noCredentialProducesNoAuthorizationKeyAtAll() {
        let headers = GatewayEndpoint.headers(for: nil)
        #expect(headers["Authorization"] == nil)
        #expect(headers.keys.contains("Authorization") == false)
        #expect(headers.isEmpty)
    }

    @Test
    func twoDifferentCredentialsProduceDifferentHeaders() {
        let first = GatewayEndpoint.headers(for: DeviceCredential(keyId: "device-1", secret: "token-one"))
        let second = GatewayEndpoint.headers(for: DeviceCredential(keyId: "device-2", secret: "token-two"))
        #expect(first != second)
    }

    /// Caller-supplied headers survive alongside the authorization entry
    /// a credential contributes, as long as the caller does not supply
    /// its own `Authorization` key — see the two collision tests below
    /// for what happens when it does.
    @Test
    func explicitlySuppliedHeadersArePreservedAlongsideTheAuthorizationHeader() {
        let credential = DeviceCredential(keyId: "device-1", secret: "the-secret-token")
        let headers = GatewayEndpoint.headers(
            for: credential,
            additionalHeaders: ["X-Client-Version": "1.0"]
        )
        #expect(headers == [
            "Authorization": "Bearer the-secret-token",
            "X-Client-Version": "1.0"
        ])
    }

    /// Caller-supplied headers are preserved even with no credential.
    @Test
    func explicitlySuppliedHeadersArePreservedWithNoCredential() {
        let headers = GatewayEndpoint.headers(for: nil, additionalHeaders: ["X-Client-Version": "1.0"])
        #expect(headers == ["X-Client-Version": "1.0"])
    }

    /// The credential's entry always wins on an `Authorization` key
    /// collision: the caller-supplied value is discarded, not merged or
    /// preserved alongside it.
    @Test
    func aCallerSuppliedAuthorizationHeaderIsOverwrittenByACredential() {
        let credential = DeviceCredential(keyId: "device-1", secret: "the-secret-token")
        let headers = GatewayEndpoint.headers(
            for: credential,
            additionalHeaders: ["Authorization": "Bearer caller-supplied-value"]
        )
        #expect(headers == ["Authorization": "Bearer the-secret-token"])
    }

    /// A caller-supplied `Authorization` header is only meaningful when
    /// there is no credential to contribute one — in that case it passes
    /// through untouched, like any other header.
    @Test
    func aCallerSuppliedAuthorizationHeaderPassesThroughUnchangedWithNoCredential() {
        let headers = GatewayEndpoint.headers(
            for: nil,
            additionalHeaders: ["Authorization": "Bearer caller-supplied-value"]
        )
        #expect(headers == ["Authorization": "Bearer caller-supplied-value"])
    }
}
