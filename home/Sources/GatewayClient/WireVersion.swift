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
