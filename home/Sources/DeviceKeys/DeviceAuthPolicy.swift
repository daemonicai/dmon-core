import GatewayClient

/// What a `dmon-home` client should do about device-key authentication when it next
/// connects — the join of "does the store require a key" (`DevicesFileReader`), "does
/// this host hold one" (`DeviceCredentialStore`), and, when it does, "does the store still
/// vouch for the one it holds" (`DevicesFileReader.status(ofKeyId:)`).
///
/// Five states, not two. Collapsing every non-provisioning outcome into
/// `.connectUnauthenticated` would produce a silent 401 at connect time with nothing
/// naming why. This type does not act on any of them beyond naming it — provisioning a
/// credential, and the two refusals' shared escape hatch (deleting the stored credential),
/// are both separate, later or operator work.
public enum DeviceAuthDecision: Sendable {
    /// Present this credential on connect — the store still lists this host's `keyId` as
    /// active.
    case presentCredential(DeviceCredential)
    /// Connect without presenting a key — the store has no active entries.
    case connectUnauthenticated
    /// The store has at least one active entry, but this host holds no credential of its
    /// own. The only state that ever provisions one (separate, later work).
    case credentialRequiredButMissing
    /// This host's held credential's `keyId` is in the store, but revoked — an operator
    /// act, not something this client should route around by provisioning a new one.
    /// `message` names the escape hatch: delete the stored credential to return to
    /// `.credentialRequiredButMissing`.
    case credentialRevoked(message: String)
    /// This host's held credential's `keyId` does not appear in the store at all — the
    /// store may have been replaced, restored, or copied from another machine. Refused for
    /// the same reason as `.credentialRevoked`: this client must not silently re-provision
    /// access the store does not currently record. `message` names the same escape hatch.
    case credentialUnknownToStore(message: String)
}

extension DeviceAuthDecision: Equatable {
    /// `DeviceCredential` (`GatewayClient`) does not itself conform to `Equatable`, so
    /// `.presentCredential` compares its payload's `keyId` and `secret` directly rather
    /// than deriving conformance from it.
    public static func == (lhs: DeviceAuthDecision, rhs: DeviceAuthDecision) -> Bool {
        switch (lhs, rhs) {
        case (.presentCredential(let left), .presentCredential(let right)):
            // `==` on `secret` here is test-convenience equality, not a constant-time
            // comparison. This type's only call sites today are test assertions; a future
            // security-sensitive reuse must not inherit a timing channel from this — the
            // C# side deliberately uses `CryptographicOperations.FixedTimeEquals` for
            // exactly this value.
            left.keyId == right.keyId && left.secret == right.secret
        case (.connectUnauthenticated, .connectUnauthenticated):
            true
        case (.credentialRequiredButMissing, .credentialRequiredButMissing):
            true
        case (.credentialRevoked(let left), .credentialRevoked(let right)):
            left == right
        case (.credentialUnknownToStore(let left), .credentialUnknownToStore(let right)):
            left == right
        default:
            false
        }
    }
}

/// Joins `DevicesFileReader` and `DeviceCredentialStore` into the single
/// `DeviceAuthDecision` above.
public struct DeviceAuthPolicy: Sendable {
    private let fileReader: DevicesFileReader
    private let credentialStore: any DeviceCredentialStore

    public init(fileReader: DevicesFileReader, credentialStore: any DeviceCredentialStore) {
        self.fileReader = fileReader
        self.credentialStore = credentialStore
    }

    /// Reads the devices file and, only when it requires a key, this host's own
    /// credential store, then — only when this host holds a credential — where that
    /// credential's `keyId` stands in the file, and returns the resulting
    /// `DeviceAuthDecision`. Propagates whatever `fileReader.hasActiveEntries()`,
    /// `credentialStore.loadCredential()`, or `fileReader.status(ofKeyId:)` throw — none of
    /// the three is ever swallowed into `.connectUnauthenticated`. No `catch` appears
    /// anywhere in this method; that absence, not a guard against any specific exception
    /// type, is what guarantees a read failure can never present as "no key required".
    public func decide() async throws -> DeviceAuthDecision {
        guard try fileReader.hasActiveEntries() else {
            return .connectUnauthenticated
        }
        guard let credential = try await credentialStore.loadCredential() else {
            return .credentialRequiredButMissing
        }
        switch try fileReader.status(ofKeyId: credential.keyId) {
        case .active:
            return .presentCredential(credential)
        case .revoked:
            return .credentialRevoked(message: """
                This device's stored credential (keyId "\(credential.keyId)") has been \
                revoked by the network host. Delete it with \
                `\(KeychainDeviceCredentialStore.deleteCommand)` to let this host provision \
                a new one on its next connection attempt.
                """)
        case .absent:
            return .credentialUnknownToStore(message: """
                This device's stored credential (keyId "\(credential.keyId)") is not \
                recorded by the network host's device store (it may have been replaced, \
                restored from backup, or copied from another machine). Delete it with \
                `\(KeychainDeviceCredentialStore.deleteCommand)` to let this host \
                provision a new one on its next connection attempt.
                """)
        }
    }
}
