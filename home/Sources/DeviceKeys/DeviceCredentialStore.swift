import GatewayClient

/// Where this host's own `DeviceCredential` comes from.
///
/// This is the co-located half of device-key authentication (design D13/D16-carve).
/// `GatewayClient` is written to extract onto iOS unmodified (design D16) — it holds a
/// credential and computes its hash, but never decides where one comes from. A
/// `DeviceCredentialStore` conformer may assume a filesystem, and in
/// `KeychainDeviceCredentialStore`'s case a Keychain, shared with the `Dmon.Network` host
/// it authenticates to; that assumption is exactly what makes this module co-located-only.
/// `GatewayClient` must never depend on this module or anything that depends on it — the
/// dependency runs one way, `DeviceKeys` on `GatewayClient` — so that `GatewayClient`'s
/// iOS portability build (`make dmon-home-ios-check`) never has to build host-only code
/// to prove it.
///
/// The read path (`loadCredential()`) defines only the operation it needs: loading a
/// credential a prior run already holds. `store(_:)` is the write path's addition
/// (`DeviceKeyProvisioner`) — persisting a freshly generated credential so a later
/// `loadCredential()` call finds it.
///
/// **Memory zeroing, considered and declined.** `DeviceCredential.secret` is a plain
/// `String`, and nothing between a conformer's Keychain read and this protocol's callers
/// zeroes it afterward. That is a decision, not an oversight:
///
/// - A Swift `String` cannot be reliably zeroed in the first place — copy-on-write, the
///   small-string optimisation, and bridging to `NSString` can all leave copies of the
///   content in memory that zeroing one `String`'s storage would not reach.
/// - The secret has to become a `String` regardless: `GatewayEndpoint.headers(for:)`
///   builds the `Authorization` header by interpolating it directly
///   (`"Bearer \(credential.secret)"`), so there is no lower-level byte buffer this type
///   could keep the plaintext out of even if it wanted to.
/// - The process holding it is unsandboxed (`home/project.yml` sets no entitlements),
///   runs as the same user account that owns `devices.json`, and reads this credential
///   from an already-*unlocked* Keychain. Anyone positioned to scrape this process's
///   unzeroed memory for the secret is already positioned to read `devices.json` or query
///   the Keychain item directly — zeroing would not close off an attacker's easiest path
///   to the same secret.
///
/// Not worth it for this threat model.
public protocol DeviceCredentialStore: Sendable {
    /// This host's own device credential, or `nil` if none has been provisioned yet.
    func loadCredential() async throws -> DeviceCredential?

    /// Persists `credential` as this host's own device credential. Called exactly once per
    /// provisioning attempt, by `DeviceKeyProvisioner`, after it has already verified this
    /// host holds none — conformers are not expected to merge with or reject an existing
    /// item, only to store the one they are given.
    func store(_ credential: DeviceCredential) async throws
}
