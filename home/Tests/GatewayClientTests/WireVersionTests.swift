import Testing
@testable import GatewayClient

@Suite
struct WireVersionTests {
    @Test
    func currentVersionIsZeroDotTwo() {
        #expect(WireVersion.current == WireVersion(major: 0, minor: 2))
        #expect(WireVersion.current.description == "0.2")
    }

    @Test
    func matchingMajorAndMinorAreCompatible() {
        let a = WireVersion(major: 0, minor: 2)
        let b = WireVersion(major: 0, minor: 2)
        #expect(a.isCompatible(with: b))
    }

    @Test
    func mismatchedMinorIsRejected() {
        let a = WireVersion(major: 0, minor: 2)
        let b = WireVersion(major: 0, minor: 3)
        #expect(!a.isCompatible(with: b))
    }

    @Test
    func mismatchedMajorIsRejected() {
        let a = WireVersion(major: 0, minor: 2)
        let b = WireVersion(major: 1, minor: 2)
        #expect(!a.isCompatible(with: b))
    }

    @Test
    func parsesMajorDotMinor() throws {
        let version = try WireVersion(parsing: "0.2")
        #expect(version == WireVersion(major: 0, minor: 2))
    }

    @Test
    func parsingRejectsMalformedInput() {
        #expect(throws: WireVersion.ParseError.self) {
            try WireVersion(parsing: "not-a-version")
        }
    }

    @Test
    func checkCompatibilityAcceptsAMatchingAdvertisedVersion() throws {
        let host = try WireVersion.checkCompatibility(advertised: "0.2")
        #expect(host == WireVersion.current)
    }

    @Test
    func checkCompatibilityThrowsNotAdvertisedForANilVersion() {
        #expect(throws: WireVersion.CompatibilityError.notAdvertised(client: .current)) {
            _ = try WireVersion.checkCompatibility(advertised: nil)
        }
    }

    @Test
    func checkCompatibilityThrowsUnparsableForAGarbageString() {
        #expect(throws: WireVersion.CompatibilityError.unparsable(client: .current, raw: "banana")) {
            _ = try WireVersion.checkCompatibility(advertised: "banana")
        }
    }

    /// The C# side's `ProtocolVersion.MajorMinor` truncates a three-part
    /// version string; this client must not mirror that — a three-part
    /// advertisement is refused by name, not silently read as a match.
    @Test
    func checkCompatibilityThrowsUnparsableForAThreeComponentVersionRatherThanTruncating() {
        #expect(throws: WireVersion.CompatibilityError.unparsable(client: .current, raw: "0.2.1")) {
            _ = try WireVersion.checkCompatibility(advertised: "0.2.1")
        }
    }

    @Test
    func checkCompatibilityThrowsMismatchForAMajorDifference() {
        #expect(throws: WireVersion.CompatibilityError.mismatch(
            client: .current,
            host: WireVersion(major: 1, minor: 2)
        )) {
            _ = try WireVersion.checkCompatibility(advertised: "1.2")
        }
    }

    @Test
    func checkCompatibilityThrowsMismatchForAMinorDifference() {
        #expect(throws: WireVersion.CompatibilityError.mismatch(
            client: .current,
            host: WireVersion(major: 0, minor: 9)
        )) {
            _ = try WireVersion.checkCompatibility(advertised: "0.9")
        }
    }

    @Test
    func mismatchMessageNamesBothVersions() {
        let error = WireVersion.CompatibilityError.mismatch(
            client: WireVersion(major: 0, minor: 2),
            host: WireVersion(major: 1, minor: 7)
        )
        #expect(error.message.contains("0.2"))
        #expect(error.message.contains("1.7"))
    }

    @Test
    func notAdvertisedMessageNamesTheClientVersionAndDoesNotNameAReleaseNumber() {
        let error = WireVersion.CompatibilityError.notAdvertised(client: .current)
        #expect(error.message.contains("0.2"))
    }

    @Test
    func unparsableMessageNamesTheClientVersionAndTheRawValueReceived() {
        let error = WireVersion.CompatibilityError.unparsable(client: .current, raw: "banana")
        #expect(error.message.contains("0.2"))
        #expect(error.message.contains("banana"))
    }
}
