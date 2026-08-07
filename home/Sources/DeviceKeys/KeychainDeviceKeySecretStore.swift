import Foundation
import GatewayClient
import Security

/// Stores this host's own `DeviceKeySecret` in the macOS Keychain, as one
/// `kSecClassGenericPassword` item:
///
/// - `kSecAttrService`: `"ai.daemonic.dmon-home.device-credential"` — this app's own
///   Keychain service name (bundle id prefix `ai.daemonic.dmon-home`, `home/project.yml`),
///   distinct from anything the network host or another app might use.
/// - `kSecAttrAccount`: `"default"` — this host holds exactly one secret of its own,
///   so a single fixed account name is enough; there is no per-device or per-store
///   variation to key on.
/// - `kSecValueData`: the secret's `keyId` and `secret`, encoded together as JSON.
///   `keyId` alone is not secret, but the two are always read and written together, and
///   one Keychain item is simpler than splitting them across a config file (where `keyId`
///   could live in the clear) and the Keychain (for `secret` alone) — and it keeps the
///   secret out of config by construction, which is what the spec's "the secret is not
///   exposed" scenario asserts.
///
/// `store(_:)` writes a new item with `SecItemAdd`, falling back to `SecItemUpdate` if one
/// already exists (defensive only — `DeviceKeyProvisioner` never calls `store(_:)` without
/// having first confirmed `load()` returned `nil`, and `load()` throws
/// rather than returning `nil` for an item that exists but fails to decode, so an
/// undecodable-but-present item cannot reach this path either).
///
/// The one window this leaves open: a genuine concurrent write landing between that
/// `load()` check and this call. Only then can `SecItemAdd` see
/// `errSecDuplicateItem` for an item this store never observed as absent — and the item
/// that landed there in that race could be a perfectly usable secret, which
/// `SecItemUpdate` then overwrites without any signal that it did. `store(_:)` does not
/// detect or guard against this; it relies entirely on the precondition its caller
/// establishes. `DeviceKeyProvisioner` has no call site yet, so the window is unreachable
/// today — whoever wires one up is responsible for whether concurrent provisioning becomes
/// possible, and for closing this window first if it does.
///
/// Linking `Security` blocks nothing by itself — the framework and its Keychain APIs exist
/// on iOS too. What actually keeps this type host-only is that it is simply not in
/// `make dmon-home-ios-check`'s build graph: that gate builds only the `GatewayClient`
/// scheme (`xcodebuild -scheme GatewayClient`), and `DeviceKeySecretStore`'s conformers —
/// this one included — sit on the other side of the one-way dependency described on that
/// protocol. Never exercised by the automated suite either: a `swift test` run must not
/// read or write the real Keychain. `DeviceAuthPolicy` and everything built on
/// `DeviceKeySecretStore` are tested against `InMemoryDeviceKeySecretStore` instead.
public struct KeychainDeviceKeySecretStore: DeviceKeySecretStore {
    private static let service = "ai.daemonic.dmon-home.device-credential"
    private static let account = "default"

    /// The `security` invocation that deletes this store's Keychain item, returning this
    /// host to "holds no secret" — the operator's escape hatch after a revoked or
    /// unknown-to-the-store secret, and the cleanup step named when `DeviceKeyProvisioner`
    /// stores a secret but then fails to append it to `devices.json`. Derived from
    /// `service`/`account` above rather than duplicated as a literal in `DeviceAuthPolicy`'s
    /// refusal messages, so the two cannot drift.
    public static let deleteCommand = "security delete-generic-password -a \(account) -s \(service)"

    public init() {}

    public func load() async throws -> DeviceKeySecret? {
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
                throw KeychainDeviceKeySecretStoreError.unreadableItem
            }
            return try DeviceKeySecretCodec.decode(data)
        case errSecItemNotFound:
            return nil
        default:
            throw KeychainDeviceKeySecretStoreError.osStatus(status)
        }
    }

    public func store(_ secret: DeviceKeySecret) async throws {
        let data = try DeviceKeySecretCodec.encode(secret)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: Self.account
        ]

        let addQuery = query.merging([kSecValueData as String: data]) { _, new in new }
        let addStatus = SecItemAdd(addQuery as CFDictionary, nil)
        if addStatus == errSecSuccess {
            return
        }

        // An item already exists (defensive path — see this type's doc comment): update it
        // in place rather than deleting and re-adding, so a failure here can never leave the
        // account with no item at all.
        guard addStatus == errSecDuplicateItem else {
            throw KeychainDeviceKeySecretStoreError.osStatus(addStatus)
        }
        let updateStatus = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        guard updateStatus == errSecSuccess else {
            throw KeychainDeviceKeySecretStoreError.osStatus(updateStatus)
        }
    }
}

/// Errors `KeychainDeviceKeySecretStore` can raise. Never carries the secret itself — a
/// raw `OSStatus` or a fixed case naming the failure mode, nothing that could reconstruct
/// or reveal the stored secret.
public enum KeychainDeviceKeySecretStoreError: Error, Sendable, Equatable {
    /// The Keychain item existed but its stored data did not decode as a secret.
    case unreadableItem
    /// A secret could not be encoded to the `Data` a Keychain item stores. Not reachable
    /// with today's `StoredSecret` (two plain `String` fields cannot fail to encode as
    /// JSON), but `store(_:)` does not force-unwrap the encode, so a future field that could
    /// fail has somewhere to surface rather than crashing.
    case unencodableSecret
    /// `SecItemCopyMatching`, `SecItemAdd`, or `SecItemUpdate` returned a status other than
    /// `errSecSuccess` (or, for `SecItemCopyMatching`, `errSecItemNotFound`).
    case osStatus(OSStatus)
}

/// The `Data ↔ DeviceKeySecret` JSON shape stored in the Keychain item's
/// `kSecValueData`. Pure parsing with no Keychain dependency of its own — split out of
/// `KeychainDeviceKeySecretStore` so it is reachable by name, and `internal` rather than
/// `private` so `@testable import DeviceKeys` can reach it directly. `SecItemCopyMatching`
/// and the `AnyObject → Data` cast above are the only parts of this store a `swift test`
/// run cannot exercise; everything else, including this codec, can and should be tested
/// without touching the real Keychain.
enum DeviceKeySecretCodec {
    static func decode(_ data: Data) throws -> DeviceKeySecret {
        let stored: StoredSecret
        do {
            stored = try JSONDecoder().decode(StoredSecret.self, from: data)
        } catch {
            throw KeychainDeviceKeySecretStoreError.unreadableItem
        }
        return DeviceKeySecret(keyId: stored.keyId, secret: stored.secret)
    }

    /// The write side of `decode` above — used by `store(_:)` to produce the `Data` written
    /// into `kSecValueData`. Symmetric with `decode`: the same `StoredSecret` shape, so a
    /// value this encodes is guaranteed to be what `decode` reads back.
    static func encode(_ secret: DeviceKeySecret) throws -> Data {
        let stored = StoredSecret(keyId: secret.keyId, secret: secret.secret)
        do {
            return try JSONEncoder().encode(stored)
        } catch {
            throw KeychainDeviceKeySecretStoreError.unencodableSecret
        }
    }

    /// Field names (`keyId`, `secret`) are the literal wire shape already sitting in every
    /// existing Keychain item — `Codable`'s synthesized `CodingKeys` derive directly from
    /// these stored-property names with no `CodingKeys` override anywhere in this type, so
    /// renaming a property here would silently change the JSON keys this codec reads and
    /// writes. Only the *type* name changed in this rename (`StoredCredential` →
    /// `StoredSecret`); the two properties are untouched.
    private struct StoredSecret: Codable {
        let keyId: String
        let secret: String
    }
}
