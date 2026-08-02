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
}
