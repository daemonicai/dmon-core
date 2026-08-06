import Foundation

/// Reads `devices.json` and reports whether the store currently requires a device key, and
/// (via `status(ofKeyId:)`) whether a specific credential is the one the store currently
/// vouches for.
///
/// Mirrors `Dmon.Network`'s own reader
/// (`frontends/Dmon.Network/DeviceKeys/DeviceKeyStoreReader.cs`) on the four facts that
/// matter to a client deciding whether to present a credential:
///
/// - **Absent file → no active entries.** Same as the host: first-run state, auth
///   disabled.
/// - **"No active entries" means every entry is revoked, not that the file is empty.**
///   A file containing only revoked rows reports `false` here, exactly as the host's own
///   reader excludes revoked entries from its active set before deciding whether a key is
///   required.
/// - **An entry with a missing or blank `secretHash` is not active either.** The host's
///   `Parse` excludes an entry when `string.IsNullOrWhiteSpace(dto.SecretHash)` is true, in
///   addition to excluding revoked ones, before that same active set gates whether a key is
///   required at all. Not because a blank hash could ever match a presented token — it
///   cannot: `DeviceKeyAuthenticator.Authenticate` always hashes the presented token to a
///   32-byte SHA-256 digest, a blank hash hex-decodes to a zero-length array, and
///   `CryptographicOperations.FixedTimeEquals` returns `false` whenever the two spans'
///   lengths differ, so a blank-hash entry matches nothing. It is excluded because an entry
///   that never carried a real secret never vouched for anything — counting it as active
///   would claim the store enforces a key it cannot actually check. This reader excludes it
///   too, on the same null-or-whitespace test.
/// - **`schemaVersion` other than `1` throws, rather than being tolerated.** Matches the
///   host, which throws on any other value.
///
/// Malformed or unreadable content also throws, and is never reported the same way as
/// "no active entries" — collapsing the two would make a parse failure look like disabled
/// auth, which is the opposite of what the store actually requires.
///
/// Unknown JSON fields, in either the envelope or an entry, are ignored — the same
/// lenient parse `Dmon.Network`'s `System.Text.Json` deserialisation performs, so a
/// future field such as `expiresAt` does not break this reader either.
public struct DevicesFileReader: Sendable {
    public let directory: URL

    /// `~/.dmon/network` — mirrors `Dmon.Network`'s own default
    /// (`frontends/Dmon.Network/Program.cs`'s `deviceKeyStoreDir` fallback). A default
    /// value a caller may override, not a path baked into this reader's logic — the same
    /// principle `GatewayEndpoint.defaultURL` applies to the connection endpoint, and
    /// what makes this reader testable against a temporary directory.
    public static let defaultDirectory = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent(".dmon", isDirectory: true)
        .appendingPathComponent("network", isDirectory: true)

    public init(directory: URL = DevicesFileReader.defaultDirectory) {
        self.directory = directory
    }

    /// Whether `devices.json` in `directory` currently has at least one active entry — one
    /// that is both non-revoked and carries a non-blank `secretHash`. `false` covers an
    /// absent file, a file whose every entry is revoked, and a file whose only
    /// non-revoked entry has a missing or blank `secretHash` — all indistinguishable to a
    /// caller deciding whether to present a key. Throws `DevicesFileError` for unreadable
    /// content, malformed JSON, or an unsupported `schemaVersion`; never returns `false`
    /// for those.
    public func hasActiveEntries() throws -> Bool {
        guard let envelope = try loadEnvelope() else {
            return false
        }
        return envelope.devices.contains { $0.revokedAt == nil && !Self.isBlank($0.secretHash) }
    }

    /// Where a given `keyId` stands in `devices.json` — the question a client holding a
    /// credential must answer before presenting it: is this the store's own record of that
    /// credential, or has the ground under it shifted?
    ///
    /// - `.active`: an entry with this `keyId` exists, is unrevoked, and carries a
    ///   non-blank `secretHash` — the store still vouches for this credential.
    /// - `.revoked`: an entry with this `keyId` exists and carries a `revokedAt` — the
    ///   store withdrew it deliberately.
    /// - `.absent`: no entry with this `keyId` exists, **or** one does but its
    ///   `secretHash` is blank. The two are folded together on purpose: an entry that never
    ///   carried a real secret never vouched for anything, so a `keyId` match against it is
    ///   not meaningfully different from no match at all — and it was never revoked, so
    ///   `.revoked` would overstate what happened. An absent `devices.json` also answers
    ///   `.absent` for any `keyId`, on the same "this store never recorded it" reasoning as
    ///   `hasActiveEntries()` treating an absent file as no active entries.
    ///
    /// Throws `DevicesFileError` under the same conditions as `hasActiveEntries()` —
    /// unreadable content, malformed JSON, or an unsupported `schemaVersion` — and never
    /// folds those into `.absent`.
    public func status(ofKeyId keyId: String) throws -> DeviceKeyIdStatus {
        guard let envelope = try loadEnvelope() else {
            return .absent
        }
        guard let entry = envelope.devices.first(where: { $0.keyId == keyId }) else {
            return .absent
        }
        if entry.revokedAt != nil {
            return .revoked
        }
        if Self.isBlank(entry.secretHash) {
            return .absent
        }
        return .active
    }

    /// Reads and decodes `devices.json`, or `nil` if the file does not exist. Throws
    /// `DevicesFileError` for unreadable content, malformed JSON, or an unsupported
    /// `schemaVersion` — the one parse path `hasActiveEntries()` and `status(ofKeyId:)`
    /// share, so both fail the same way on the same bad input.
    private func loadEnvelope() throws -> DevicesFileEnvelope? {
        let path = directory.appendingPathComponent("devices.json")
        guard FileManager.default.fileExists(atPath: path.path) else {
            return nil
        }

        let data: Data
        do {
            data = try Data(contentsOf: path)
        } catch {
            throw DevicesFileError.unreadable
        }

        let envelope: DevicesFileEnvelope
        do {
            envelope = try JSONDecoder().decode(DevicesFileEnvelope.self, from: data)
        } catch {
            throw DevicesFileError.malformedJSON
        }

        guard envelope.schemaVersion == 1 else {
            throw DevicesFileError.unsupportedSchemaVersion(envelope.schemaVersion)
        }

        return envelope
    }

    /// Mirrors `string.IsNullOrWhiteSpace` (`DeviceKeyStoreReader.Parse`): `nil`, empty, or
    /// whitespace-only all count as blank.
    ///
    /// The two definitions are not identical for arbitrary input, and the difference is
    /// recorded here rather than glossed: `CharacterSet.whitespacesAndNewlines` is a strict
    /// superset of .NET's `char.IsWhiteSpace`, adding **U+200B ZERO WIDTH SPACE** (a
    /// Foundation/ICU quirk — U+200B is category `Cf`, so it is not whitespace under the
    /// formal Unicode definition .NET follows). Every other code point through U+3100 agrees.
    /// So this side can call blank what the host would not — which cannot arise for the only
    /// value ever passed here, a hex-encoded SHA-256 digest written by the provisioning path,
    /// since a `[0-9a-f]` string cannot be composed of zero-width spaces. The claim above is
    /// therefore true for every input that can occur, not for every input expressible.
    private static func isBlank(_ value: String?) -> Bool {
        guard let value else { return true }
        return value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

/// Errors `DevicesFileReader.hasActiveEntries()` and `status(ofKeyId:)` can raise.
/// `devices.json` itself never holds a raw secret (only key ids and hashes), but these
/// carry nothing beyond what identifies the failure mode, matching the no-secret-leakage
/// discipline the rest of `DeviceKeys` follows.
public enum DevicesFileError: Error, Sendable, Equatable {
    /// The file exists but could not be read.
    case unreadable
    /// The file's content is not valid JSON, or does not match the expected envelope
    /// shape.
    case malformedJSON
    /// The envelope's `schemaVersion` was not `1`.
    case unsupportedSchemaVersion(Int)
}

/// Where a `keyId` stands relative to `devices.json`, as answered by
/// `DevicesFileReader.status(ofKeyId:)`. See that method's doc comment for what each case
/// means and why a blank-`secretHash` match is folded into `.absent` rather than treated
/// as a fourth case.
public enum DeviceKeyIdStatus: Sendable, Equatable {
    case active
    case revoked
    case absent
}

private struct DevicesFileEnvelope: Decodable {
    let schemaVersion: Int
    let devices: [DevicesFileEntry]
}

private struct DevicesFileEntry: Decodable {
    let keyId: String?
    let revokedAt: String?
    let secretHash: String?
}
