import GatewayClient
@testable import DeviceKeys

/// A `DeviceCredentialStore` conformer that never touches the Keychain: a test constructs
/// it with the credential (or lack of one) it wants `loadCredential()` to report. This is
/// what `DeviceAuthPolicyTests` drives instead of `KeychainDeviceCredentialStore` — a
/// `swift test` run must never prompt or touch the real Keychain.
struct InMemoryDeviceCredentialStore: DeviceCredentialStore {
    private let credential: DeviceCredential?

    init(credential: DeviceCredential? = nil) {
        self.credential = credential
    }

    func loadCredential() async throws -> DeviceCredential? {
        credential
    }
}
