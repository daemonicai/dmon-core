import Foundation
import Testing
import GatewayClient
@testable import DeviceKeys

/// `KeychainCredentialCodec.decode`/`encode` are the pure `Data ↔ DeviceCredential` parsing
/// `KeychainDeviceCredentialStore` delegates to. These tests exercise both directly, without
/// touching the real Keychain — `SecItemCopyMatching`, `SecItemAdd`, and `SecItemUpdate`
/// remain the only untestable surface in that store.
@Suite
struct KeychainCredentialCodecTests {
    @Test
    func aWellFormedPayloadRoundTripsToTheRightKeyIdAndSecret() throws {
        let data = Data(#"{"keyId":"device-1","secret":"super-secret-token"}"#.utf8)

        let credential = try KeychainCredentialCodec.decode(data)

        #expect(credential.keyId == "device-1")
        #expect(credential.secret == "super-secret-token")
    }

    @Test
    func malformedJSONThrowsUnreadableItemRatherThanCrashing() {
        let data = Data("not json at all".utf8)

        #expect(throws: KeychainDeviceCredentialStoreError.unreadableItem) {
            try KeychainCredentialCodec.decode(data)
        }
    }

    /// A payload cut off mid-value — as could happen from a partially-written or
    /// corrupted Keychain item — must fail the same way a wholly malformed one does.
    @Test
    func aTruncatedPayloadThrowsUnreadableItemRatherThanCrashing() {
        let data = Data(#"{"keyId":"device-1","secret":"super"#.utf8)

        #expect(throws: KeychainDeviceCredentialStoreError.unreadableItem) {
            try KeychainCredentialCodec.decode(data)
        }
    }

    @Test
    func encodeThenDecodeRoundTripsToTheOriginalKeyIdAndSecret() throws {
        let credential = DeviceCredential(keyId: "device-1", secret: "super-secret-token")

        let data = try KeychainCredentialCodec.encode(credential)
        let decoded = try KeychainCredentialCodec.decode(data)

        #expect(decoded.keyId == credential.keyId)
        #expect(decoded.secret == credential.secret)
    }

    /// `encode` produces exactly the `{"keyId":...,"secret":...}` shape `decode` above reads
    /// — pinned directly, rather than only through the round trip, so a change to either
    /// side's field names is caught even if it happened to change both consistently.
    @Test
    func encodeProducesTheKeyIdAndSecretFieldNamesDecodeExpects() throws {
        let credential = DeviceCredential(keyId: "device-1", secret: "super-secret-token")

        let data = try KeychainCredentialCodec.encode(credential)
        let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]

        #expect(object?["keyId"] as? String == "device-1")
        #expect(object?["secret"] as? String == "super-secret-token")
    }
}
