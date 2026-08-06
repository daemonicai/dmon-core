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

    /// Corrected to the block-B8 semantics: `decide()` now compares the held credential's
    /// `keyId` against the file, so the fixture must actually contain an active entry for
    /// "host" — the very gap this block closes. Under the old (pre-B8) logic this test
    /// passed even with a mismatched `keyId` ("a" in the file, "host" held), which is
    /// exactly the silent-401 hole `.credentialUnknownToStore` now catches.
    @Test
    func activeEntriesWithAHeldCredentialPresentsThatCredential() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "host")) ]
        }
        """, in: dir)
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
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "host")) ]
        }
        """, in: dir)
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

    /// Call 1's third and fourth states, at the policy layer: the held credential's
    /// `keyId` matches an *active* entry among several — present it, not refuse it merely
    /// because other entries exist.
    @Test
    func aHeldCredentialMatchingAnActiveEntryAmongSeveralPresentsIt() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [
            \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")),
            \(DevicesFileFixture.deviceEntryJSON(keyId: "host")),
            \(DevicesFileFixture.deviceEntryJSON(keyId: "revoked-device", revokedAt: "2026-06-01T00:00:00Z"))
          ]
        }
        """, in: dir)
        let credential = DeviceCredential(keyId: "host", secret: "super-secret")

        let policy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: dir),
            credentialStore: InMemoryDeviceCredentialStore(credential: credential)
        )
        #expect(try await policy.decide() == .presentCredential(credential))
    }

    /// Call 1's revoked state: the held credential's own `keyId` is revoked, while
    /// *other* active entries exist — the file requires a key overall, so this must not
    /// fall through to `.connectUnauthenticated`, and it must not trigger provisioning
    /// either (`.credentialRequiredButMissing` is for "holds none", not "holds a revoked
    /// one"). This is the "operator did this deliberately" refusal.
    @Test
    func aHeldCredentialMatchingARevokedEntryAlongsideOtherActiveEntriesIsRefusedAsRevoked() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [
            \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")),
            \(DevicesFileFixture.deviceEntryJSON(keyId: "host", revokedAt: "2026-06-01T00:00:00Z"))
          ]
        }
        """, in: dir)
        let credential = DeviceCredential(keyId: "host", secret: "super-secret")

        let policy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: dir),
            credentialStore: InMemoryDeviceCredentialStore(credential: credential)
        )
        let decision = try await policy.decide()
        guard case .credentialRevoked(let message) = decision else {
            Issue.record("expected .credentialRevoked, got \(decision.caseLabelForDiagnostics)")
            return
        }
        #expect(message.contains(KeychainDeviceCredentialStore.deleteCommand))
        #expect(message.contains(credential.keyId))
        #expect(!message.contains("super-secret"))
    }

    /// Call 1's absent state: the held credential's `keyId` does not appear in the file
    /// at all, even though the file requires a key overall — "the store may have been
    /// replaced" refusal, distinct in wording from the revoked one.
    @Test
    func aHeldCredentialAbsentFromTheFileIsRefusedAsUnknownToStore() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: dir)
        let credential = DeviceCredential(keyId: "host", secret: "super-secret")

        let policy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: dir),
            credentialStore: InMemoryDeviceCredentialStore(credential: credential)
        )
        let decision = try await policy.decide()
        guard case .credentialUnknownToStore(let message) = decision else {
            Issue.record("expected .credentialUnknownToStore, got \(decision.caseLabelForDiagnostics)")
            return
        }
        #expect(message.contains(KeychainDeviceCredentialStore.deleteCommand))
        #expect(message.contains(credential.keyId))
        #expect(!message.contains("super-secret"))
    }

    /// The revoked and absent refusals must read differently to an operator — "you did
    /// this deliberately" versus "the store may have been replaced" — even though both
    /// name the same escape hatch.
    @Test
    func theRevokedAndAbsentRefusalMessagesDiffer() async throws {
        let credential = DeviceCredential(keyId: "host", secret: "super-secret")

        let revokedDir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: revokedDir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [
            \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")),
            \(DevicesFileFixture.deviceEntryJSON(keyId: "host", revokedAt: "2026-06-01T00:00:00Z"))
          ]
        }
        """, in: revokedDir)
        let revokedPolicy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: revokedDir),
            credentialStore: InMemoryDeviceCredentialStore(credential: credential)
        )
        guard case .credentialRevoked(let revokedMessage) = try await revokedPolicy.decide() else {
            Issue.record("expected .credentialRevoked")
            return
        }

        let absentDir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: absentDir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: absentDir)
        let absentPolicy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: absentDir),
            credentialStore: InMemoryDeviceCredentialStore(credential: credential)
        )
        guard case .credentialUnknownToStore(let absentMessage) = try await absentPolicy.decide() else {
            Issue.record("expected .credentialUnknownToStore")
            return
        }

        #expect(revokedMessage != absentMessage)
        #expect(revokedMessage.contains("revoked"))
        #expect(absentMessage.contains("not recorded"))
    }

    /// Call 1's blank-`secretHash` trap, carried through to the policy: a matching but
    /// never-active entry must not be treated as a live match. It is not `.presentCredential`
    /// — deliberately folded into the absent refusal (see `DeviceKeyIdStatus.absent`'s doc
    /// comment), not the revoked one, since `revokedAt` was never set here.
    @Test
    func aHeldCredentialMatchingAnUnrevokedButBlankSecretHashEntryIsNotPresented() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [
            \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")),
            \(DevicesFileFixture.deviceEntryJSON(keyId: "host", secretHash: ""))
          ]
        }
        """, in: dir)
        let credential = DeviceCredential(keyId: "host", secret: "super-secret")

        let policy = DeviceAuthPolicy(
            fileReader: DevicesFileReader(directory: dir),
            credentialStore: InMemoryDeviceCredentialStore(credential: credential)
        )
        let decision = try await policy.decide()
        #expect(decision != .presentCredential(credential))
        guard case .credentialUnknownToStore = decision else {
            Issue.record("expected .credentialUnknownToStore, got \(decision.caseLabelForDiagnostics)")
            return
        }
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

    // MARK: - Nested redaction (reviewer nit: does redaction survive nesting?)

    /// Block B6 pinned that `DeviceCredential`'s redacting `description`/`debugDescription`
    /// hold under direct conversion. The B8 review asked the follow-up question those tests
    /// never covered: `.presentCredential`'s payload *is* a `DeviceCredential` — does
    /// redaction survive when it is nested inside `DeviceAuthDecision`, which has no
    /// `CustomStringConvertible` conformance of its own and so falls back to Swift's default,
    /// reflection-based description?
    ///
    /// Empirically, yes. Swift's default description for a type with no conformance of its
    /// own still calls `description`/`debugDescription` on each associated value that
    /// provides one, rather than reflecting straight through to its stored properties — verified
    /// directly against the toolchain, not inferred. This test pins that so a future Swift
    /// toolchain change, or a future `DeviceAuthDecision` case whose payload does not redact,
    /// cannot regress it unnoticed. It is not a substitute for care at each call site: nothing
    /// in the language enforces this the way `Equatable` conformance is enforced, which is why
    /// this file's own failure diagnostics use `caseLabelForDiagnostics` below rather than
    /// interpolating the decision directly.
    @Test
    func aDecisionCarryingACredentialDoesNotExposeTheSecretThroughStringConversion() {
        let credential = DeviceCredential(keyId: "host", secret: "super-secret-token")
        let decision = DeviceAuthDecision.presentCredential(credential)

        #expect(!"\(decision)".contains("super-secret-token"))
        #expect(!String(describing: decision).contains("super-secret-token"))
        #expect(!String(reflecting: decision).contains("super-secret-token"))
    }
}

extension DeviceAuthDecision {
    /// Case name only, deliberately never the associated payload. Used solely in this
    /// file's failure diagnostics so a mistaken assertion can never end up interpolating a
    /// `DeviceCredential` into a `swift test`/CI log — even though nesting is verified safe
    /// above, diagnostics should not depend on that continuing to hold.
    fileprivate var caseLabelForDiagnostics: String {
        switch self {
        case .presentCredential: "presentCredential"
        case .connectUnauthenticated: "connectUnauthenticated"
        case .credentialRequiredButMissing: "credentialRequiredButMissing"
        case .credentialRevoked: "credentialRevoked"
        case .credentialUnknownToStore: "credentialUnknownToStore"
        }
    }
}
