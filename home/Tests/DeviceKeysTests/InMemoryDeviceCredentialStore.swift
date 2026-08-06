import GatewayClient
@testable import DeviceKeys

/// A `DeviceCredentialStore` conformer that never touches the Keychain: a test constructs it
/// with the credential (or lack of one) it wants `loadCredential()` to report, and can
/// observe or fail the `store(_:)` call `DeviceKeyProvisioner` makes. This is what
/// `DeviceAuthPolicyTests` and `DeviceKeyProvisionerTests` drive instead of
/// `KeychainDeviceCredentialStore` — a `swift test` run must never prompt or touch the real
/// Keychain.
///
/// An `actor`, not a `struct`: `store(_:)` must be observable by a later `loadCredential()`
/// call (the round trip `DeviceKeyProvisionerTests` exercises) and `storeCallCount` must be
/// inspectable after the fact, both of which need shared mutable state a value type cannot
/// provide across the `async` boundary these methods cross.
actor InMemoryDeviceCredentialStore: DeviceCredentialStore {
    private var credential: DeviceCredential?
    private let storeError: (any Error)?
    private(set) var storeCallCount = 0

    /// - Parameters:
    ///   - credential: What `loadCredential()` reports until (and unless) `store(_:)`
    ///     replaces it.
    ///   - storeError: When non-`nil`, `store(_:)` throws this instead of recording the
    ///     credential — simulates a Keychain write failure without touching the real
    ///     Keychain. Never carries a secret itself; the tests that use it assert the
    ///     provisioner's resulting error message does not either.
    init(credential: DeviceCredential? = nil, storeError: (any Error)? = nil) {
        self.credential = credential
        self.storeError = storeError
    }

    func loadCredential() async throws -> DeviceCredential? {
        credential
    }

    func store(_ credential: DeviceCredential) async throws {
        storeCallCount += 1
        if let storeError {
            throw storeError
        }
        self.credential = credential
    }
}
