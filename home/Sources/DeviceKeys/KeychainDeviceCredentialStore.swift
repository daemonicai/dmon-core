import Foundation
import GatewayClient
import Security

/// Stores this host's own `DeviceCredential` in the macOS Keychain, as one
/// `kSecClassGenericPassword` item:
///
/// - `kSecAttrService`: `"ai.daemonic.dmon-home.device-credential"` — this app's own
///   Keychain service name (bundle id prefix `ai.daemonic.dmon-home`, `home/project.yml`),
///   distinct from anything the network host or another app might use.
/// - `kSecAttrAccount`: `"default"` — this host holds exactly one credential of its own,
///   so a single fixed account name is enough; there is no per-device or per-store
///   variation to key on.
/// - `kSecValueData`: the credential's `keyId` and `secret`, encoded together as JSON.
///   `keyId` alone is not secret, but the two are always read and written together, and
///   one Keychain item is simpler than splitting them across a config file (where `keyId`
///   could live in the clear) and the Keychain (for `secret` alone) — and it keeps the
///   secret out of config by construction, which is what the spec's "the secret is not
///   exposed" scenario asserts.
///
/// This block implements the read side only (`loadCredential()`); writing a new item is
/// separate, later work.
///
/// Linking `Security` blocks nothing by itself — the framework and its Keychain APIs exist
/// on iOS too. What actually keeps this type host-only is that it is simply not in
/// `make dmon-home-ios-check`'s build graph: that gate builds only the `GatewayClient`
/// scheme (`xcodebuild -scheme GatewayClient`), and `DeviceCredentialStore`'s conformers —
/// this one included — sit on the other side of the one-way dependency described on that
/// protocol. Never exercised by the automated suite either: a `swift test` run must not
/// read or write the real Keychain. `DeviceAuthPolicy` and everything built on
/// `DeviceCredentialStore` are tested against `InMemoryDeviceCredentialStore` instead.
public struct KeychainDeviceCredentialStore: DeviceCredentialStore {
    private static let service = "ai.daemonic.dmon-home.device-credential"
    private static let account = "default"

    /// The `security` invocation that deletes this store's Keychain item, returning this
    /// host to "holds no credential" — the provisioning path (task 6.5's write side,
    /// separate work). Derived from `service`/`account` above rather than duplicated as a
    /// literal in `DeviceAuthPolicy`'s refusal messages, so the two cannot drift.
    public static let deleteCommand = "security delete-generic-password -a \(account) -s \(service)"

    public init() {}

    public func loadCredential() async throws -> DeviceCredential? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: Self.account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]

        var item: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &item)

        switch status {
        case errSecSuccess:
            guard let data = item as? Data else {
                throw KeychainDeviceCredentialStoreError.unreadableItem
            }
            return try KeychainCredentialCodec.decode(data)
        case errSecItemNotFound:
            return nil
        default:
            throw KeychainDeviceCredentialStoreError.osStatus(status)
        }
    }
}

/// Errors `KeychainDeviceCredentialStore` can raise. Never carries the secret itself — a
/// raw `OSStatus` or a fixed case naming the failure mode, nothing that could reconstruct
/// or reveal the stored credential.
public enum KeychainDeviceCredentialStoreError: Error, Sendable, Equatable {
    /// The Keychain item existed but its stored data did not decode as a credential.
    case unreadableItem
    /// `SecItemCopyMatching` returned a status other than `errSecSuccess` or
    /// `errSecItemNotFound`.
    case osStatus(OSStatus)
}

/// The `Data ↔ DeviceCredential` JSON shape stored in the Keychain item's
/// `kSecValueData`. Pure parsing with no Keychain dependency of its own — split out of
/// `KeychainDeviceCredentialStore` so it is reachable by name, and `internal` rather than
/// `private` so `@testable import DeviceKeys` can reach it directly. `SecItemCopyMatching`
/// and the `AnyObject → Data` cast above are the only parts of this store a `swift test`
/// run cannot exercise; everything else, including this codec, can and should be tested
/// without touching the real Keychain.
///
/// `decode` only for now (this block is the read path); a symmetric `encode` for writing a
/// new Keychain item is later work (task 6.5's write path) and belongs alongside it here.
enum KeychainCredentialCodec {
    static func decode(_ data: Data) throws -> DeviceCredential {
        let stored: StoredCredential
        do {
            stored = try JSONDecoder().decode(StoredCredential.self, from: data)
        } catch {
            throw KeychainDeviceCredentialStoreError.unreadableItem
        }
        return DeviceCredential(keyId: stored.keyId, secret: stored.secret)
    }

    private struct StoredCredential: Codable {
        let keyId: String
        let secret: String
    }
}
