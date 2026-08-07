import Foundation
import Testing
import GatewayClient
@testable import DeviceKeys

/// `DeviceKeySecretCodec.decode`/`encode` are the pure `Data ↔ DeviceKeySecret` parsing
/// `KeychainDeviceKeySecretStore` delegates to. These tests exercise both directly, without
/// touching the real Keychain — `SecItemCopyMatching`, `SecItemAdd`, and `SecItemUpdate`
/// remain the only untestable surface in that store.
@Suite
struct DeviceKeySecretCodecTests {
    @Test
    func aWellFormedPayloadRoundTripsToTheRightKeyIdAndSecret() throws {
        let data = Data(#"{"keyId":"device-1","secret":"super-secret-token"}"#.utf8)

        let secret = try DeviceKeySecretCodec.decode(data)

        #expect(secret.keyId == "device-1")
        #expect(secret.secret == "super-secret-token")
    }

    @Test
    func malformedJSONThrowsUnreadableItemRatherThanCrashing() {
        let data = Data("not json at all".utf8)

        #expect(throws: KeychainDeviceKeySecretStoreError.unreadableItem) {
            try DeviceKeySecretCodec.decode(data)
        }
    }

    /// A payload cut off mid-value — as could happen from a partially-written or
    /// corrupted Keychain item — must fail the same way a wholly malformed one does.
    @Test
    func aTruncatedPayloadThrowsUnreadableItemRatherThanCrashing() {
        let data = Data(#"{"keyId":"device-1","secret":"super"#.utf8)

        #expect(throws: KeychainDeviceKeySecretStoreError.unreadableItem) {
            try DeviceKeySecretCodec.decode(data)
        }
    }

    @Test
    func encodeThenDecodeRoundTripsToTheOriginalKeyIdAndSecret() throws {
        let secret = DeviceKeySecret(keyId: "device-1", secret: "super-secret-token")

        let data = try DeviceKeySecretCodec.encode(secret)
        let decoded = try DeviceKeySecretCodec.decode(data)

        #expect(decoded.keyId == secret.keyId)
        #expect(decoded.secret == secret.secret)
    }

    /// `encode` produces exactly the `{"keyId":...,"secret":...}` shape `decode` above reads
    /// — pinned directly, rather than only through the round trip, so a change to either
    /// side's field names is caught even if it happened to change both consistently. These
    /// two field names are the literal JSON already sitting in every existing Keychain item
    /// (see `DeviceKeySecretCodec.StoredSecret`'s doc comment) — this test is what catches a
    /// future property rename in that private struct silently orphaning them.
    @Test
    func encodeProducesTheKeyIdAndSecretFieldNamesDecodeExpects() throws {
        let secret = DeviceKeySecret(keyId: "device-1", secret: "super-secret-token")

        let data = try DeviceKeySecretCodec.encode(secret)
        let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]

        #expect(object?["keyId"] as? String == "device-1")
        #expect(object?["secret"] as? String == "super-secret-token")
    }
}
