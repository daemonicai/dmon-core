import Foundation
import Testing
import GatewayClient
@testable import DeviceKeys

@Suite
struct DeviceAuthPolicyTests {
    @Test
    func anAbsentFileConnectsUnauthenticated() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }

        let policy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: dir),
            credentialStore: InMemoryDeviceCredentialStore()
        )
        #expect(try await policy.decide() == .connectUnauthenticated)
    }

    /// Call 3's trap, at the policy layer: a store with entries but none active must
    /// still connect unauthenticated, even though this host holds a credential it could
    /// present.
    @Test
    func aStoreWithOnlyRevokedEntriesConnectsUnauthenticatedEvenWithAHeldCredential() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile(#"""
        {
          "schemaVersion": 1,
          "devices": [ { "keyId": "a", "revokedAt": "2026-01-01T00:00:00Z" } ]
        }
        """#, in: dir)

        let policy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: dir),
            credentialStore: InMemoryDeviceCredentialStore(
                credential: DeviceCredential(keyId: "host", secret: "held-secret")
            )
        )
        #expect(try await policy.decide() == .connectUnauthenticated)
    }

    @Test
    func activeEntriesWithAHeldCredentialPresentsThatCredential() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile(#"""
        {
          "schemaVersion": 1,
          "devices": [ { "keyId": "a", "revokedAt": null, "secretHash": "hash-a" } ]
        }
        """#, in: dir)
        let credential = DeviceCredential(keyId: "host", secret: "super-secret")

        let policy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: dir),
            credentialStore: InMemoryDeviceCredentialStore(credential: credential)
        )
        #expect(try await policy.decide() == .presentCredential(credential))
    }

    /// Ties the policy's output to `GatewayEndpoint.headers(for:)` — the credential the
    /// policy names is the one that actually ends up in the `Authorization` header, not
    /// merely a value of the right shape.
    @Test
    func thePresentedCredentialIsTheOneGatewayEndpointPutsInTheHeader() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile(#"""
        {
          "schemaVersion": 1,
          "devices": [ { "keyId": "a", "revokedAt": null, "secretHash": "hash-a" } ]
        }
        """#, in: dir)
        let credential = DeviceCredential(keyId: "host", secret: "super-secret")

        let policy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: dir),
            credentialStore: InMemoryDeviceCredentialStore(credential: credential)
        )
        guard case .presentCredential(let presented) = try await policy.decide() else {
            Issue.record("expected .presentCredential")
            return
        }
        let headers = GatewayEndpoint.headers(for: presented)
        #expect(headers["Authorization"] == "Bearer super-secret")
    }

    @Test
    func activeEntriesWithNoHeldCredentialReportsCredentialRequiredButMissing() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile(#"""
        {
          "schemaVersion": 1,
          "devices": [ { "keyId": "a", "revokedAt": null, "secretHash": "hash-a" } ]
        }
        """#, in: dir)

        let policy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: dir),
            credentialStore: InMemoryDeviceCredentialStore()
        )
        #expect(try await policy.decide() == .credentialRequiredButMissing)
    }

    @Test
    func anUnsupportedSchemaVersionPropagatesAsAnErrorThroughThePolicy() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile(#"""
        {
          "schemaVersion": 7,
          "devices": []
        }
        """#, in: dir)

        let policy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: dir),
            credentialStore: InMemoryDeviceCredentialStore()
        )
        await #expect(throws: DevicesFileError.unsupportedSchemaVersion(7)) {
            try await policy.decide()
        }
    }

    /// The policy layer's own fail-open trap: a malformed store must not be swallowed
    /// into `.connectUnauthenticated` on its way through `decide()`.
    @Test
    func malformedJSONPropagatesAsAnErrorRatherThanConnectingUnauthenticated() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("{ not valid json", in: dir)

        let policy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: dir),
            credentialStore: InMemoryDeviceCredentialStore()
        )
        await #expect(throws: DevicesFileError.malformedJSON) {
            try await policy.decide()
        }
    }
}
