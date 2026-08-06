import GatewayClient

/// What a `dmon-home` client should do about device-key authentication when it next
/// connects — the join of "does the store require a key" (`DevicesFileReader`) and "does
/// this host hold one" (`DeviceCredentialStore`).
///
/// Three states, not two. Collapsing the third into `.connectUnauthenticated` would
/// produce a silent 401 at connect time with nothing naming why: the store requires a
/// key and this host simply does not hold one yet. This type does not act on that case
/// beyond naming it — provisioning a credential in response is separate, later work.
public enum DeviceAuthDecision: Sendable {
    /// Present this credential on connect — the store has at least one active entry, and
    /// this host holds a credential of its own.
    case presentCredential(DeviceCredential)
    /// Connect without presenting a key — the store has no active entries.
    case connectUnauthenticated
    /// The store has at least one active entry, but this host holds no credential of its
    /// own.
    case credentialRequiredButMissing
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
    /// credential store, then returns the resulting `DeviceAuthDecision`. Propagates
    /// whatever `fileReader.hasActiveEntries()` or `credentialStore.loadCredential()`
    /// throw — neither is ever swallowed into `.connectUnauthenticated`.
    public func decide() async throws -> DeviceAuthDecision {
        guard try fileReader.hasActiveEntries() else {
            return .connectUnauthenticated
        }
        guard let credential = try await credentialStore.loadCredential() else {
            return .credentialRequiredButMissing
        }
        return .presentCredential(credential)
    }
}
