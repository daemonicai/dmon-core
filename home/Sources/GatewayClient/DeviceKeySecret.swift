import CryptoKit
import Foundation

/// The client's own device key secret, presented to `Dmon.Network` as an
/// `Authorization: Bearer <secret>` header when the host's device-key store
/// requires it.
///
/// This type is the *portable* half of device-key authentication (design
/// D13/D16-carve): it holds a secret and computes its hash, but it
/// neither generates one nor persists one — where the secret comes
/// from (Keychain, `devices.json` self-provisioning) is the host-facing
/// half, decided elsewhere.
///
/// `secret` is the raw bearer token, not its hash — the wire presents the
/// token itself; only the *stored* form on the host side is a hash
/// (`secretHash`, below), so it never has to see the plaintext again.
public struct DeviceKeySecret: Sendable {
    public let keyId: String
    public let secret: String

    public init(keyId: String, secret: String) {
        self.keyId = keyId
        self.secret = secret
    }

    /// Hex-encoded, lowercase SHA-256 digest of `secret`'s UTF-8 bytes —
    /// mirrors `Dmon.Network`'s stored `secretHash` column exactly, so a
    /// divergence from `DeviceKeyAuthenticator`'s comparison fails loudly
    /// rather than merely producing a client that cannot authenticate
    /// (design risk 2). See `DeviceKeySecretTests` for the pinned digests
    /// this must continue to reproduce.
    public var secretHash: String {
        Self.secretHash(ofToken: secret)
    }

    /// Static so device-key-secret-store code (the host-facing half of
    /// task 6.5) can hash a bare token — e.g. when self-provisioning a new
    /// entry into `devices.json` — without constructing a full secret
    /// first. `secretHash` above delegates here; this is the only SHA-256
    /// computation in this type.
    public static func secretHash(ofToken token: String) -> String {
        let digest = SHA256.hash(data: Data(token.utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }
}

extension DeviceKeySecret: CustomStringConvertible, CustomDebugStringConvertible {
    /// Redacts `secret`. `GatewayConnection` logs errors with
    /// `String(describing: error)|(reflecting:)`, and any error type that
    /// ever carries a secret must not leak it through that
    /// path — the key id alone is enough to identify which secret was
    /// in play.
    public var description: String {
        "DeviceKeySecret(keyId: \"\(keyId)\", secret: <redacted>)"
    }

    /// Same redaction as `description` — `String(reflecting:)` must not
    /// recover the secret either, since without this conformance it would
    /// fall back to reflecting every stored property, `secret` included.
    public var debugDescription: String { description }
}
