/// A `Major.Minor` wire protocol version, as advertised during the gateway
/// handshake.
///
/// Two versions are compatible when both the major and minor components
/// match exactly; there is no forward- or backward-compatibility window.
public struct WireVersion: Hashable, Sendable, CustomStringConvertible {
    public let major: Int
    public let minor: Int

    public init(major: Int, minor: Int) {
        self.major = major
        self.minor = minor
    }

    /// The wire protocol version this host speaks.
    public static let current = WireVersion(major: 0, minor: 2)

    public var description: String { "\(major).\(minor)" }

    /// Whether `self` and `other` agree on both major and minor components.
    public func isCompatible(with other: WireVersion) -> Bool {
        major == other.major && minor == other.minor
    }
}

extension WireVersion {
    public enum ParseError: Error, Equatable {
        case malformed(String)
    }

    /// Parses a `Major.Minor` string such as `"0.2"`.
    public init(parsing string: String) throws {
        let parts = string.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 2,
              let major = Int(parts[0]),
              let minor = Int(parts[1])
        else {
            throw ParseError.malformed(string)
        }
        self.major = major
        self.minor = minor
    }
}

extension WireVersion {
    /// Why `checkCompatibility(advertised:)` refused to proceed. Three
    /// cases, not one, because each names a different actionable cause:
    /// a version was received and understood but does not match
    /// (`mismatch`), no version was received at all (`notAdvertised`), or
    /// one was received but could not be read as `Major.Minor`
    /// (`unparsable`). Collapsing these into one case would still let a
    /// caller name what happened, but not what to do about it.
    public enum CompatibilityError: Error, Equatable, Sendable {
        /// The host advertised a version that parsed cleanly but whose
        /// major or minor component differs from this client's.
        case mismatch(client: WireVersion, host: WireVersion)
        /// The `attached` frame carried no `wire` field at all — a host
        /// that predates it. Not a pass: the requirement this type
        /// implements is explicit that compatibility must never be
        /// assumed from a version the client did not receive.
        case notAdvertised(client: WireVersion)
        /// The `attached` frame carried a `wire` field that is not a
        /// `Major.Minor` pair (wrong shape, or more than two
        /// components — this client never truncates a longer version
        /// string into a match).
        case unparsable(client: WireVersion, raw: String)

        /// A human-readable, actionable message: names both versions
        /// where there are two to name, and says what a person should
        /// do about the mismatch rather than only describing it.
        public var message: String {
            switch self {
            case .mismatch(let client, let host):
                return "Wire protocol version mismatch: this client speaks \(client), the host advertised \(host). Upgrade whichever side is behind so both speak the same Major.Minor version."
            case .notAdvertised(let client):
                return "The host did not advertise a wire protocol version on attach; this client speaks \(client) and cannot assume compatibility without one. Upgrade the host to a version that advertises its wire version on attach."
            case .unparsable(let client, let raw):
                return "The host advertised an unreadable wire protocol version (\"\(raw)\"); this client speaks \(client) and expects a Major.Minor pair such as \"0.2\". Check the host's wire version string."
            }
        }
    }

    /// The one place the "is this host compatible?" policy lives.
    /// `advertised` is the `wire` field decoded from an `attached`
    /// frame, exactly as received — `nil` if the frame carried none.
    ///
    /// Returns the host's parsed `WireVersion` when it is present,
    /// parses as `Major.Minor`, and matches `WireVersion.current` in
    /// both components. Otherwise throws the `CompatibilityError` that
    /// names the specific way it did not: `.notAdvertised` for a `nil`
    /// input, `.unparsable` for a value `WireVersion(parsing:)` cannot
    /// read, `.mismatch` for a value that parses but disagrees with
    /// `.current`.
    public static func checkCompatibility(advertised: String?) throws -> WireVersion {
        guard let advertised else {
            throw CompatibilityError.notAdvertised(client: .current)
        }
        guard let host = try? WireVersion(parsing: advertised) else {
            throw CompatibilityError.unparsable(client: .current, raw: advertised)
        }
        guard WireVersion.current.isCompatible(with: host) else {
            throw CompatibilityError.mismatch(client: .current, host: host)
        }
        return host
    }
}
