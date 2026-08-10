import GatewayClient
@testable import DeviceKeys

/// A `DeviceKeySecretStore` conformer that never touches the Keychain: a test constructs it
/// with the secret (or lack of one) it wants `load()` to report, and can
/// observe or fail the `store(_:)` call `DeviceKeyProvisioner` makes. This is what
/// `DeviceAuthPolicyTests` and `DeviceKeyProvisionerTests` drive instead of
/// `KeychainDeviceKeySecretStore` — a `swift test` run must never prompt or touch the real
/// Keychain.
///
/// An `actor`, not a `struct`: `store(_:)` must be observable by a later `load()`
/// call (the round trip `DeviceKeyProvisionerTests` exercises) and `storeCallCount` must be
/// inspectable after the fact, both of which need shared mutable state a value type cannot
/// provide across the `async` boundary these methods cross.
actor InMemoryDeviceKeySecretStore: DeviceKeySecretStore {
    private var secret: DeviceKeySecret?
    private let storeError: (any Error)?
    private(set) var storeCallCount = 0

    /// - Parameters:
    ///   - secret: What `load()` reports until (and unless) `store(_:)`
    ///     replaces it.
    ///   - storeError: When non-`nil`, `store(_:)` throws this instead of recording the
    ///     secret — simulates a Keychain write failure without touching the real
    ///     Keychain. Never carries a secret itself; the tests that use it assert the
    ///     provisioner's resulting error message does not either.
    init(secret: DeviceKeySecret? = nil, storeError: (any Error)? = nil) {
        self.secret = secret
        self.storeError = storeError
    }

    func load() async throws -> DeviceKeySecret? {
        secret
    }

    func store(_ secret: DeviceKeySecret) async throws {
        storeCallCount += 1
        if let storeError {
            throw storeError
        }
        self.secret = secret
    }
}
