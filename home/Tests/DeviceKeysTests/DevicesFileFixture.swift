import Foundation

/// Shared temp-directory fixture for `DeviceKeysTests` — an isolated directory a test can
/// point a `DevicesFileReader` at, optionally seeded with a `devices.json`. Pure
/// filesystem setup; not a double for anything under test.
enum DevicesFileFixture {
    /// A `secretHash` shaped like the hex-encoded SHA-256 digest the real store writes —
    /// 64 lowercase hex characters. Not an actual digest of anything; only its shape
    /// matters to the reader under test.
    static let plausibleSecretHash = String(repeating: "0123456789abcdef", count: 4)

    static func makeTempDirectory() -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try! FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    static func writeDevicesFile(_ content: String, in directory: URL) throws {
        let path = directory.appendingPathComponent("devices.json")
        try content.write(to: path, atomically: true, encoding: .utf8)
    }

    /// One `devices.json` entry with every field the real host writes — `keyId`, `name`,
    /// a hex-SHA-256-shaped `secretHash`, an ISO-8601 `createdAt`, and `revokedAt` null or
    /// a timestamp — so a fixture built from this cannot silently drift into a shape the
    /// real store never produces (`frontends/Dmon.Network/DeviceKeys/DevicesFileEnvelope.cs`,
    /// mirrored by `PerDeviceKeyE2ETests.DeviceEntry` on the C# side).
    static func deviceEntryJSON(
        keyId: String,
        secretHash: String = DevicesFileFixture.plausibleSecretHash,
        revokedAt: String? = nil
    ) -> String {
        let revokedField = revokedAt.map { "\"\($0)\"" } ?? "null"
        return """
        { "keyId": "\(keyId)", "name": "\(keyId)-display-name", "secretHash": "\(secretHash)", "createdAt": "2026-01-01T00:00:00Z", "revokedAt": \(revokedField) }
        """
    }
}
