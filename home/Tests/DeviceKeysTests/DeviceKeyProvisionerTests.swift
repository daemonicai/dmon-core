import Foundation
import Testing
import GatewayClient
@testable import DeviceKeys

@Suite
struct DeviceKeyProvisionerTests {
    // MARK: - Call 4: unreachable except from the state that warrants it

    /// The empty-store case: an absent `devices.json` can never reach `.keyRequiredButMissing`
    /// through `DeviceAuthPolicy`, and `provision()` re-derives the same check itself rather
    /// than trusting a caller-supplied decision, so it must refuse here too — and touch
    /// neither the Keychain nor the file while doing so.
    @Test
    func provisioningAgainstAnAbsentStoreThrowsNotRequiredWithoutTouchingAnything() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }

        let store = InMemoryDeviceKeySecretStore()
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)

        await #expect(throws: DeviceKeyProvisioningError.notRequired) {
            _ = try await provisioner.provision()
        }
        #expect(await store.storeCallCount == 0)
        #expect(!FileManager.default.fileExists(atPath: dir.appendingPathComponent("devices.json").path))
    }

    /// A store whose only entries are revoked also fails `hasActiveEntries()`, exactly as it
    /// does for `DeviceAuthPolicy` — provisioning must not treat "the file exists" as
    /// sufficient.
    @Test
    func provisioningAgainstAStoreWithOnlyRevokedEntriesThrowsNotRequiredWithoutModifyingTheFile() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let content = """
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "a", revokedAt: "2026-01-01T00:00:00Z")) ]
        }
        """
        try DevicesFileFixture.writeDevicesFile(content, in: dir)

        let store = InMemoryDeviceKeySecretStore()
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)

        await #expect(throws: DeviceKeyProvisioningError.notRequired) {
            _ = try await provisioner.provision()
        }
        #expect(await store.storeCallCount == 0)
        let after = try String(contentsOf: dir.appendingPathComponent("devices.json"), encoding: .utf8)
        #expect(after == content)
    }

    /// This host already holding a key secret is the other half of "not required" —
    /// `.keyRequiredButMissing` names holding *none* as part of the precondition.
    @Test
    func provisioningWhenThisHostAlreadyHoldsAKeyThrowsNotRequired() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let content = """
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """
        try DevicesFileFixture.writeDevicesFile(content, in: dir)

        let held = DeviceKeySecret(keyId: "already-held", secret: "already-held-secret")
        let store = InMemoryDeviceKeySecretStore(secret: held)
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)

        await #expect(throws: DeviceKeyProvisioningError.notRequired) {
            _ = try await provisioner.provision()
        }
        #expect(await store.storeCallCount == 0)
        let after = try String(contentsOf: dir.appendingPathComponent("devices.json"), encoding: .utf8)
        #expect(after == content)
    }

    // MARK: - The round trip

    /// The strongest test available: provision against a temp directory, then read the
    /// result back with the existing `DevicesFileReader`/`DeviceAuthPolicy` and confirm the
    /// decision is now `.presentKey` carrying the key `provision()` returned.
    /// The file this test reads is the one the code under test wrote — not hand-rolled to
    /// match it.
    @Test
    func provisioningThenDecidingPresentsTheGeneratedKey() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: dir)

        let store = InMemoryDeviceKeySecretStore()
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)
        let secret = try await provisioner.provision()

        let policy = DeviceAuthPolicy(fileReader: DevicesFileReader(directory: dir), secretStore: store)
        let decision = try await policy.decide()
        #expect(decision == .presentKey(secret))
    }

    // MARK: - Shape of the appended entry

    @Test
    func theAppendedEntryHasTheFieldsDevicesFileEnvelopeExpects() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: dir)

        let store = InMemoryDeviceKeySecretStore()
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)
        let secret = try await provisioner.provision()

        let entry = try Self.entry(forKeyId: secret.keyId, in: dir)
        #expect(entry["keyId"] as? String == secret.keyId)
        #expect((entry["name"] as? String)?.isEmpty == false)
        #expect(entry["secretHash"] as? String == secret.secretHash)
        #expect(entry["createdAt"] is String)
        // Per D13/spec: absent or null, never a real revocation timestamp for a fresh entry.
        if let revokedAt = entry["revokedAt"] {
            #expect(revokedAt is NSNull)
        }
        // Exact key set, not merely "these are present" — this is the only automated guard
        // on the appended shape matching `DevicesFileEnvelope.cs`'s
        // `PropertyNameCaseInsensitive = false` reader, so it must fail on an added or
        // renamed key, not just a missing one.
        #expect(Set(entry.keys) == ["keyId", "name", "secretHash", "createdAt"])
    }

    /// Wired, not recomputed: a second hashing site is exactly the divergence B6's pinned
    /// digests exist to prevent.
    @Test
    func secretHashInTheFileEqualsDeviceKeySecretSecretHashOfTheGeneratedToken() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: dir)

        let store = InMemoryDeviceKeySecretStore()
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)
        let secret = try await provisioner.provision()

        let entry = try Self.entry(forKeyId: secret.keyId, in: dir)
        #expect(entry["secretHash"] as? String == DeviceKeySecret.secretHash(ofToken: secret.secret))
    }

    /// Pins the exact form: `yyyy-MM-ddTHH:mm:ssZ`, the extended ISO-8601 form with an
    /// explicit UTC offset that .NET's `DateTimeOffset.Parse` accepts — matching every
    /// `createdAt` `DevicesFileFixture` writes elsewhere in this suite.
    @Test
    func createdAtIsTheExactISO8601FormDateTimeOffsetParses() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: dir)

        var components = DateComponents()
        components.year = 2026
        components.month = 3
        components.day = 15
        components.hour = 9
        components.minute = 30
        components.second = 45
        components.timeZone = TimeZone(identifier: "UTC")
        let fixedDate = Calendar(identifier: .gregorian).date(from: components)!

        let store = InMemoryDeviceKeySecretStore()
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store, now: { fixedDate })
        let secret = try await provisioner.provision()

        let entry = try Self.entry(forKeyId: secret.keyId, in: dir)
        #expect(entry["createdAt"] as? String == "2026-03-15T09:30:45Z")
    }

    // MARK: - Preserving what this client does not model

    /// The hazard this block names as the single worst outcome available: a round trip
    /// through a lossy DTO would delete fields this client does not model. Parsed
    /// generically instead, so a pre-existing row — including a field this client has never
    /// heard of — must survive the append with every field intact.
    @Test
    func preExistingRowsSurviveTheAppendWithEveryFieldIntact() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [
            {
              "keyId": "other-device",
              "name": "Other Device Display Name",
              "secretHash": "\(DevicesFileFixture.plausibleSecretHash)",
              "createdAt": "2025-06-01T00:00:00Z",
              "revokedAt": null,
              "expiresAt": "2027-06-01T00:00:00Z"
            }
          ]
        }
        """, in: dir)

        let store = InMemoryDeviceKeySecretStore()
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)
        _ = try await provisioner.provision()

        let envelope = try Self.envelope(in: dir)
        let devices = envelope["devices"] as? [[String: Any]] ?? []
        #expect(devices.count == 2)

        let preserved = devices.first { ($0["keyId"] as? String) == "other-device" }
        #expect(preserved?["name"] as? String == "Other Device Display Name")
        #expect(preserved?["secretHash"] as? String == DevicesFileFixture.plausibleSecretHash)
        #expect(preserved?["createdAt"] as? String == "2025-06-01T00:00:00Z")
        #expect(preserved?["expiresAt"] as? String == "2027-06-01T00:00:00Z")
        if let revokedAt = preserved?["revokedAt"] {
            #expect(revokedAt is NSNull)
        }
    }

    /// `schemaVersion` is carried forward from what was read, never reasserted as `1` by the
    /// writer itself.
    @Test
    func schemaVersionIsPreservedAsRead() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: dir)

        let store = InMemoryDeviceKeySecretStore()
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)
        _ = try await provisioner.provision()

        let envelope = try Self.envelope(in: dir)
        #expect(envelope["schemaVersion"] as? Int == 1)
    }

    // MARK: - Distinct generation

    /// Falsifies a constant or a seeded generator.
    @Test
    func twoProvisioningsProduceDifferentTokensAndKeyIds() async throws {
        let dirA = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dirA) }
        let dirB = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dirB) }
        let seed = """
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """
        try DevicesFileFixture.writeDevicesFile(seed, in: dirA)
        try DevicesFileFixture.writeDevicesFile(seed, in: dirB)

        let secretA = try await DeviceKeyProvisioner(
            fileReader: DevicesFileReader(directory: dirA),
            secretStore: InMemoryDeviceKeySecretStore()
        ).provision()
        let secretB = try await DeviceKeyProvisioner(
            fileReader: DevicesFileReader(directory: dirB),
            secretStore: InMemoryDeviceKeySecretStore()
        ).provision()

        #expect(secretA.keyId != secretB.keyId)
        #expect(secretA.secret != secretB.secret)
    }

    // MARK: - Token shape

    /// Pins what `generateToken`'s doc comment claims about the shape of a generated
    /// secret: no whitespace, and every character within standard base64's alphabet. No
    /// Swift-only test can catch the cross-language `DeviceKeyAuthenticator` interaction
    /// this shape exists for, but this catches a future change to token generation —
    /// switching to a different encoding, say — that would break it.
    @Test
    func generatedSecretContainsNoWhitespaceAndOnlyStandardBase64Characters() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: dir)

        let store = InMemoryDeviceKeySecretStore()
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)
        let secret = try await provisioner.provision()

        #expect(!secret.secret.isEmpty)
        #expect(secret.secret.rangeOfCharacter(from: .whitespacesAndNewlines) == nil)
        let standardBase64Alphabet = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=")
        #expect(secret.secret.unicodeScalars.allSatisfy { standardBase64Alphabet.contains($0) })
    }

    // MARK: - Call 1: the failure ordering

    /// Keychain-write failure must leave `devices.json` completely untouched — this host
    /// holds nothing new and the store gained no row, the same state as if provisioning had
    /// never been attempted.
    @Test
    func aKeychainWriteFailureDoesNotTouchTheFileAtAll() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let content = """
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """
        try DevicesFileFixture.writeDevicesFile(content, in: dir)

        let store = InMemoryDeviceKeySecretStore(storeError: StubStoreError.keychainDenied)
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)

        await #expect(throws: DeviceKeyProvisioningError.self) {
            _ = try await provisioner.provision()
        }
        #expect(await store.storeCallCount == 1)
        let after = try String(contentsOf: dir.appendingPathComponent("devices.json"), encoding: .utf8)
        #expect(after == content)
    }

    /// A Keychain write that fails must surface as `.keychainWriteFailed`, and the
    /// message must never contain the token that was about to be stored.
    @Test
    func aKeychainWriteFailureSurfacesAsKeychainWriteFailedWithoutLeakingATokenEverGenerated() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: dir)

        let store = InMemoryDeviceKeySecretStore(storeError: StubStoreError.keychainDenied)
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)

        do {
            _ = try await provisioner.provision()
            Issue.record("expected provision() to throw")
        } catch DeviceKeyProvisioningError.keychainWriteFailed(let message) {
            #expect(message.contains("keychainDenied"))
        } catch {
            Issue.record("expected .keychainWriteFailed, got \(error)")
        }
    }

    /// The other half of call 1: a `devices.json` append failure *after* a successful
    /// Keychain write must surface an error naming the cleanup command, because this host
    /// now holds a key secret the store has no record of.
    @Test
    func anAppendFailureAfterAKeychainSuccessNamesTheCleanupCommand() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer {
            try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: dir.path)
            try? FileManager.default.removeItem(at: dir)
        }
        let content = """
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """
        try DevicesFileFixture.writeDevicesFile(content, in: dir)
        // Read/execute only: appendEntry can still read the existing file, but cannot create
        // the temp file it needs to write the replacement — forcing the append to fail after
        // the (in-memory, not-real) Keychain write above it has already succeeded.
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: dir.path)

        let store = InMemoryDeviceKeySecretStore()
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)

        do {
            _ = try await provisioner.provision()
            Issue.record("expected provision() to throw")
        } catch DeviceKeyProvisioningError.devicesFileAppendFailed(let keyId, let message) {
            #expect(!keyId.isEmpty)
            #expect(message.contains(KeychainDeviceKeySecretStore.deleteCommand))
            #expect(message.contains(keyId))
        } catch {
            Issue.record("expected .devicesFileAppendFailed, got \(error)")
        }
        #expect(await store.storeCallCount == 1)

        // Guaranteed transitively today (the 0o500 directory blocks temp-file creation
        // before any write is attempted, and 0o500 still permits reading a file already
        // inside it) — asserted directly so that guarantee is stated by the test, not left
        // implicit in the fixture, exactly like the Keychain-failure sibling above.
        let after = try String(contentsOf: dir.appendingPathComponent("devices.json"), encoding: .utf8)
        #expect(after == content)
    }

    // MARK: - Permissions

    @Test
    func theRewrittenFileIsOwnerReadWriteOnly() async throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [ \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")) ]
        }
        """, in: dir)

        let store = InMemoryDeviceKeySecretStore()
        let provisioner = DeviceKeyProvisioner(fileReader: DevicesFileReader(directory: dir), secretStore: store)
        _ = try await provisioner.provision()

        let path = dir.appendingPathComponent("devices.json").path
        let attributes = try FileManager.default.attributesOfItem(atPath: path)
        let permissions = (attributes[.posixPermissions] as? NSNumber)?.intValue ?? 0
        #expect(permissions & 0o777 == 0o600)
    }

    // MARK: - Helpers

    private static func envelope(in directory: URL) throws -> [String: Any] {
        let data = try Data(contentsOf: directory.appendingPathComponent("devices.json"))
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            Issue.record("devices.json did not decode as an object")
            return [:]
        }
        return object
    }

    private static func entry(forKeyId keyId: String, in directory: URL) throws -> [String: Any] {
        let devices = try envelope(in: directory)["devices"] as? [[String: Any]] ?? []
        guard let entry = devices.first(where: { ($0["keyId"] as? String) == keyId }) else {
            Issue.record("no entry with keyId \(keyId)")
            return [:]
        }
        return entry
    }

    private enum StubStoreError: Error {
        case keychainDenied
    }
}
