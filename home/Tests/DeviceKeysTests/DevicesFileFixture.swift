import Foundation

/// Shared temp-directory fixture for `DeviceKeysTests` — an isolated directory a test can
/// point a `DevicesFileReader` at, optionally seeded with a `devices.json`. Pure
/// filesystem setup; not a double for anything under test.
enum DevicesFileFixture {
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
}
