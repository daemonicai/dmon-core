import Foundation
import Testing
@testable import DeviceKeys

@Suite
struct DevicesFileReaderTests {
    @Test
    func anAbsentFileReportsNoActiveEntries() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.hasActiveEntries() == false)
    }

    /// The revoked-filtering trap call 3 of the brief names: "empty" means no *active*
    /// entry, not no entry at all. A reader that merely checked "is the devices array
    /// empty" would report active entries here and be wrong.
    @Test
    func aFileWithOnlyRevokedEntriesReportsNoActiveEntries() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile(#"""
        {
          "schemaVersion": 1,
          "devices": [
            { "keyId": "a", "revokedAt": "2026-01-01T00:00:00Z" }
          ]
        }
        """#, in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.hasActiveEntries() == false)
    }

    @Test
    func aFileWithAnActiveEntryReportsActiveEntries() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile(#"""
        {
          "schemaVersion": 1,
          "devices": [
            { "keyId": "a", "revokedAt": null, "secretHash": "hash-a" }
          ]
        }
        """#, in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.hasActiveEntries() == true)
    }

    /// Mirrors `DeviceKeyStoreReader.Parse`'s own blank-`secretHash` exclusion
    /// (`string.IsNullOrWhiteSpace(dto.SecretHash)`): a non-revoked entry whose
    /// `secretHash` is blank must not count as active. Not because a blank hash could ever
    /// match a presented token — `CryptographicOperations.FixedTimeEquals` rejects it on
    /// length alone before content is even compared — but because an entry that never
    /// carried a real secret never vouched for anything, so a devices.json in this shape
    /// must report the same "no key required" outcome as an absent or all-revoked file.
    @Test
    func aFileWithOnlyABlankSecretHashEntryReportsNoActiveEntries() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile(#"""
        {
          "schemaVersion": 1,
          "devices": [
            { "keyId": "a", "revokedAt": null, "secretHash": "" }
          ]
        }
        """#, in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.hasActiveEntries() == false)
    }

    @Test
    func aRevokedEntryAlongsideAnActiveOneStillReportsActiveEntries() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile(#"""
        {
          "schemaVersion": 1,
          "devices": [
            { "keyId": "a", "revokedAt": "2026-01-01T00:00:00Z" },
            { "keyId": "b", "revokedAt": null, "secretHash": "hash-b" }
          ]
        }
        """#, in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.hasActiveEntries() == true)
    }

    @Test
    func anUnsupportedSchemaVersionThrowsRatherThanReportingEmpty() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile(#"""
        {
          "schemaVersion": 2,
          "devices": []
        }
        """#, in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(throws: DevicesFileError.unsupportedSchemaVersion(2)) {
            try reader.hasActiveEntries()
        }
    }

    /// Call 3's fail-open trap: a parse failure must propagate as an error, never as
    /// the same `false` an empty/absent store would report. Asserting the thrown error
    /// case specifically (not merely that the call didn't return `true`) is what catches
    /// a reader that silently treats malformed content as "no active entries".
    @Test
    func malformedJSONThrowsRatherThanReportingNoActiveEntries() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("{ not valid json", in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(throws: DevicesFileError.malformedJSON) {
            try reader.hasActiveEntries()
        }
    }

    @Test
    func statusOfKeyIdReportsActiveForAMatchingActiveEntryAmongSeveral() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [
            \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")),
            \(DevicesFileFixture.deviceEntryJSON(keyId: "this-host"))
          ]
        }
        """, in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.status(ofKeyId: "this-host") == .active(secretHash: DevicesFileFixture.plausibleSecretHash))
    }

    /// Pins that `.active`'s `secretHash` is the matched entry's own field, read verbatim —
    /// not merely a value of the right shape (`statusOfKeyIdReportsActiveForAMatchingActiveEntryAmongSeveral`,
    /// above, would still pass if this were hard-coded to `plausibleSecretHash`). This is the
    /// field `DeviceAuthPolicy.decide()` compares against a held secret's own `secretHash` to
    /// close the B5 mismatch gap — it must be the file's value, not a placeholder.
    @Test
    func statusOfKeyIdReportsTheMatchedEntrysOwnSecretHashNotAPlaceholder() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let distinctHash = String(repeating: "fedcba9876543210", count: 4)
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [
            \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")),
            \(DevicesFileFixture.deviceEntryJSON(keyId: "this-host", secretHash: distinctHash))
          ]
        }
        """, in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.status(ofKeyId: "this-host") == .active(secretHash: distinctHash))
    }

    @Test
    func statusOfKeyIdReportsRevokedForAMatchingRevokedEntry() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [
            \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device")),
            \(DevicesFileFixture.deviceEntryJSON(keyId: "this-host", revokedAt: "2026-06-01T00:00:00Z"))
          ]
        }
        """, in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.status(ofKeyId: "this-host") == .revoked)
    }

    @Test
    func statusOfKeyIdReportsAbsentWhenNoEntryHasThatKeyId() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [
            \(DevicesFileFixture.deviceEntryJSON(keyId: "other-device"))
          ]
        }
        """, in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.status(ofKeyId: "this-host") == .absent)
    }

    /// A matching, unrevoked entry whose `secretHash` is blank never vouched for anything
    /// — it is folded into `.absent` rather than `.active` (it carries no real secret to
    /// match against) or `.revoked` (`revokedAt` is nil; nothing was withdrawn). See
    /// `status(ofKeyId:)`'s doc comment for the full reasoning.
    @Test
    func statusOfKeyIdReportsAbsentForAMatchingEntryWithABlankSecretHash() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("""
        {
          "schemaVersion": 1,
          "devices": [
            \(DevicesFileFixture.deviceEntryJSON(keyId: "this-host", secretHash: ""))
          ]
        }
        """, in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.status(ofKeyId: "this-host") == .absent)
    }

    @Test
    func statusOfKeyIdReportsAbsentForAnAbsentFile() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.status(ofKeyId: "this-host") == .absent)
    }

    /// `status(ofKeyId:)` must fail the same way `hasActiveEntries()` does — never folding
    /// a parse failure into `.absent`, which would let a broken store look identical to a
    /// store that legitimately never heard of this credential.
    @Test
    func statusOfKeyIdThrowsRatherThanReportingAbsentOnMalformedJSON() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile("{ not valid json", in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(throws: DevicesFileError.malformedJSON) {
            try reader.status(ofKeyId: "this-host")
        }
    }

    @Test
    func anUnknownFieldInTheEnvelopeAndAnEntryParsesFine() throws {
        let dir = DevicesFileFixture.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try DevicesFileFixture.writeDevicesFile(#"""
        {
          "schemaVersion": 1,
          "futureEnvelopeField": "ignored",
          "devices": [
            { "keyId": "a", "revokedAt": null, "secretHash": "hash-a", "expiresAt": "2027-01-01T00:00:00Z" }
          ]
        }
        """#, in: dir)

        let reader = DevicesFileReader(directory: dir)
        #expect(try reader.hasActiveEntries() == true)
    }
}
