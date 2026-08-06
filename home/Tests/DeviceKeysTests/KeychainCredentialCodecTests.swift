import Foundation
import Testing
@testable import DeviceKeys

/// `KeychainCredentialCodec.decode` is the pure `Data → DeviceCredential` parsing that
/// `KeychainDeviceCredentialStore` delegates to. These tests exercise it directly, without
/// touching the real Keychain — `SecItemCopyMatching` and the `AnyObject → Data` cast
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
}
