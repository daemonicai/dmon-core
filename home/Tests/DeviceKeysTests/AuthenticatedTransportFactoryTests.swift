import Foundation
import Testing
import os
import GatewayClient
@testable import DeviceKeys

/// A `GatewayTransport` this suite never actually connects, sends on, or receives from —
/// `AuthenticatedTransportFactory.makeTransport()` only ever *builds* a transport, it never
/// exercises one, so this double exists purely to satisfy the protocol.
private struct NoOpTransport: GatewayTransport {
    func connect() async throws {}
    func send(_ frame: String) async throws {}
    func receive() async throws -> String { throw GatewayTransportError.notConnected }
    func close() async {}
}

/// Records every `GatewayEndpoint` a test's `AuthenticatedTransportFactory` builds a
/// transport from, in creation order — the same "record everything, inspect after the fact"
/// shape `RecordingTransportFactory` (`GatewayClientTests`) uses for `GatewaySession`, kept
/// as its own small copy here rather than shared across test targets. `buildTransport`'s
/// closure type is synchronous and non-throwing (`AuthenticatedTransportFactory.init`'s own
/// parameter), so `OSAllocatedUnfairLock` guards the list rather than an actor.
private final class TransportBuildRecorder: Sendable {
    private let state = OSAllocatedUnfairLock<[GatewayEndpoint]>(initialState: [])

    var buildTransport: @Sendable (GatewayEndpoint) -> any GatewayTransport {
        { [self] endpoint in
            state.withLock { $0.append(endpoint) }
            return NoOpTransport()
        }
    }

    func endpoints() -> [GatewayEndpoint] {
        state.withLock { $0 }
    }
}

/// Exercises the B5 connect flow: `AuthenticatedTransportFactory.makeTransport()` turns each
/// of `DeviceAuthPolicy`'s six decisions into either a transport carrying the right
/// `Authorization` header, or a thrown `DeviceAuthConnectionRefused` — never the other way
/// around for a refusal, which is the property this whole design exists to guarantee.
@Suite
struct AuthenticatedTransportFactoryTests {
    private static let baseURL = URL(string: "ws://127.0.0.1:5500/ws")!

    @Test
    func connectUnauthenticatedBuildsATransportWithNoAuthorizationHeaderAtAll() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }

        let recorder = TransportBuildRecorder()
        let factory = AuthenticatedTransportFactory(
            endpoint: GatewayEndpoint(url: Self.baseURL),
            policy: DeviceAuthPolicy(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore()
            ),
            provisioner: DeviceKeyProvisioner(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore()
            ),
            buildTransport: recorder.buildTransport
        )

        _ = try await factory.makeTransport()

        let endpoints = recorder.endpoints()
        #expect(endpoints.count == 1)
        // Not merely "no Bearer value" — the key must not be present at all.
        #expect(!(endpoints.first?.headers.keys.contains("Authorization") ?? true))
    }

    /// A caller-supplied header on the base endpoint must still reach the built transport
    /// when there is no credential to fold in — pins that `additionalHeaders:` is wired
    /// through, not merely `Authorization` in isolation.
    @Test
    func connectUnauthenticatedStillCarriesOtherCallerSuppliedHeaders() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }

        let recorder = TransportBuildRecorder()
        let factory = AuthenticatedTransportFactory(
            endpoint: GatewayEndpoint(url: Self.baseURL, headers: ["X-Client-Version": "1.0"]),
            policy: DeviceAuthPolicy(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore()
            ),
            provisioner: DeviceKeyProvisioner(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore()
            ),
            buildTransport: recorder.buildTransport
        )

        _ = try await factory.makeTransport()

        #expect(recorder.endpoints().first?.headers["X-Client-Version"] == "1.0")
    }

    @Test
    func presentKeyBuildsATransportWithTheBearerHeader() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let secret = DeviceKeySecret(keyId: "host", secret: "super-secret")
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "host", secretHash: secret.secretHash)) ]
        }
        """, in: dir)

        let recorder = TransportBuildRecorder()
        let factory = AuthenticatedTransportFactory(
            endpoint: GatewayEndpoint(url: Self.baseURL),
            policy: DeviceAuthPolicy(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore(secret: secret)
            ),
            provisioner: DeviceKeyProvisioner(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore(secret: secret)
            ),
            buildTransport: recorder.buildTransport
        )

        _ = try await factory.makeTransport()

        #expect(recorder.endpoints().first?.headers["Authorization"] == "Bearer super-secret")
    }

    /// `.keyRequiredButMissing` is the one decision this factory acts on beyond naming it:
    /// it provisions, then presents what it just provisioned — not what was true before the
    /// call.
    @Test
    func keyRequiredButMissingProvisionsThenPresentsTheNewCredential() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: dir)

        let store = InMemoryDeviceKeySecretStore()
        let recorder = TransportBuildRecorder()
        let factory = AuthenticatedTransportFactory(
            endpoint: GatewayEndpoint(url: Self.baseURL),
            policy: DeviceAuthPolicy(fileReader: DevicesFileReader(directory: dir), secretStore: store),
            provisioner: DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store),
            buildTransport: recorder.buildTransport
        )

        _ = try await factory.makeTransport()

        let provisioned = try #require(await store.load())
        #expect(recorder.endpoints().first?.headers["Authorization"] == "Bearer \(provisioned.secret)")
    }

    /// The hazard this block names as the one that must never happen: a refusal degrading
    /// into an unauthenticated connection. Proven the strongest way available — the recorder
    /// shows `buildTransport` was never called at all, not merely that the resulting header
    /// was absent.
    @Test
    func keyRevokedThrowsAndNeverBuildsATransport() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let secret = DeviceKeySecret(keyId: "host", secret: "super-secret")
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [
            \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")),
            \(DevicesFileFixture.deviceEntryJSON(keyId: "host", revokedAt: "2026-06-01T00:00:00Z"))
          ]
        }
        """, in: dir)

        let recorder = TransportBuildRecorder()
        let factory = AuthenticatedTransportFactory(
            endpoint: GatewayEndpoint(url: Self.baseURL),
            policy: DeviceAuthPolicy(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore(secret: secret)
            ),
            provisioner: DeviceKeyProvisioner(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore(secret: secret)
            ),
            buildTransport: recorder.buildTransport
        )

        do {
            _ = try await factory.makeTransport()
            Issue.record("expected makeTransport() to throw")
        } catch let error as DeviceAuthConnectionRefused {
            #expect(error.message.contains(KeychainDeviceKeySecretStore.deleteCommand))
            #expect(!error.message.contains("super-secret"))
        } catch {
            Issue.record("expected DeviceAuthConnectionRefused, got \(error)")
        }
        #expect(recorder.endpoints().isEmpty)
    }

    @Test
    func keyUnknownToStoreThrowsAndNeverBuildsATransport() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let secret = DeviceKeySecret(keyId: "host", secret: "super-secret")
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: dir)

        let recorder = TransportBuildRecorder()
        let factory = AuthenticatedTransportFactory(
            endpoint: GatewayEndpoint(url: Self.baseURL),
            policy: DeviceAuthPolicy(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore(secret: secret)
            ),
            provisioner: DeviceKeyProvisioner(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore(secret: secret)
            ),
            buildTransport: recorder.buildTransport
        )

        do {
            _ = try await factory.makeTransport()
            Issue.record("expected makeTransport() to throw")
        } catch let error as DeviceAuthConnectionRefused {
            #expect(error.message.contains(KeychainDeviceKeySecretStore.deleteCommand))
            #expect(!error.message.contains("super-secret"))
        } catch {
            Issue.record("expected DeviceAuthConnectionRefused, got \(error)")
        }
        #expect(recorder.endpoints().isEmpty)
    }

    /// The gap this block closes, exercised end to end through the connect flow: an active
    /// but hash-mismatched credential must refuse, not 401 silently by presenting it anyway.
    @Test
    func secretMismatchThrowsAndNeverBuildsATransport() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let secret = DeviceKeySecret(keyId: "host", secret: "super-secret")
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "host")) ]
        }
        """, in: dir)
        // `deviceEntryJSON(keyId: "host")` writes `plausibleSecretHash`, not
        // `secret.secretHash` — a genuine mismatch, not a fixture bug (see
        // `DeviceAuthPolicyTests.aHeldKeyWhoseSecretHashNoLongerMatchesTheActiveEntryIsRefusedAsSecretMismatch`).
        #expect(DevicesFileFixture.plausibleSecretHash != secret.secretHash)

        let recorder = TransportBuildRecorder()
        let factory = AuthenticatedTransportFactory(
            endpoint: GatewayEndpoint(url: Self.baseURL),
            policy: DeviceAuthPolicy(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore(secret: secret)
            ),
            provisioner: DeviceKeyProvisioner(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore(secret: secret)
            ),
            buildTransport: recorder.buildTransport
        )

        do {
            _ = try await factory.makeTransport()
            Issue.record("expected makeTransport() to throw")
        } catch let error as DeviceAuthConnectionRefused {
            #expect(error.message.contains(KeychainDeviceKeySecretStore.deleteCommand))
            #expect(!error.message.contains("super-secret"))
        } catch {
            Issue.record("expected DeviceAuthConnectionRefused, got \(error)")
        }
        #expect(recorder.endpoints().isEmpty)
    }

    /// `makeTransport()` is suitable directly as `GatewaySession.init(makeTransport:)`'s
    /// closure (B5's own claim about this type) — pinned by actually wiring one up and
    /// driving a handshake through it, rather than merely matching signatures by inspection.
    @Test
    func makeTransportIsUsableDirectlyAsAGatewaySessionsMakeTransportClosure() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }

        let factory = AuthenticatedTransportFactory(
            endpoint: GatewayEndpoint(url: Self.baseURL),
            policy: DeviceAuthPolicy(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore()
            ),
            provisioner: DeviceKeyProvisioner(
                fileReader: DevicesFileReader(directory: dir),
                secretStore: InMemoryDeviceKeySecretStore()
            ),
            buildTransport: { _ in NoOpTransport() }
        )

        _ = GatewaySession(makeTransport: factory.makeTransport)
    }
}
