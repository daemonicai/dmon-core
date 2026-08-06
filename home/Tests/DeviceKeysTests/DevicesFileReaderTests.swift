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
    /// `secretHash` is blank must not count as active — an empty hash would match any
    /// constant-time comparison, so a devices.json in this shape must report the same
    /// "no key required" outcome as an absent or all-revoked file.
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
