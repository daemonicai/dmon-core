import Testing
@testable import GatewayClient

/// Close-code mapping against `frontends/Dmon.Network/NetworkConnectionEndpoint.cs`,
/// verified line-for-line against that source rather than assumed.
@Suite
struct GatewayCloseCodeTests {
    @Test
    func normalClosureMapsToOneThousand() {
        #expect(GatewayCloseCode(rawValue: 1000) == .normal)
        #expect(GatewayCloseCode.normal.rawValue == 1000)
    }

    @Test
    func messageTooBigMapsToOneThousandAndNine() {
        #expect(GatewayCloseCode(rawValue: 1009) == .messageTooBig)
        #expect(GatewayCloseCode.messageTooBig.rawValue == 1009)
    }

    @Test
    func protocolViolationMapsToFourFourZeroZero() {
        #expect(GatewayCloseCode(rawValue: 4400) == .protocolViolation)
        #expect(GatewayCloseCode.protocolViolation.rawValue == 4400)
    }

    @Test
    func unknownSessionMapsToFourFourZeroFour() {
        #expect(GatewayCloseCode(rawValue: 4404) == .unknownSession)
        #expect(GatewayCloseCode.unknownSession.rawValue == 4404)
    }

    @Test
    func supersededByNewerAttachMapsToFourFourZeroNine() {
        #expect(GatewayCloseCode(rawValue: 4409) == .supersededByNewerAttach)
        #expect(GatewayCloseCode.supersededByNewerAttach.rawValue == 4409)
    }

    @Test
    func coreFailureMapsToFourFiveZeroZero() {
        #expect(GatewayCloseCode(rawValue: 4500) == .coreFailure)
        #expect(GatewayCloseCode.coreFailure.rawValue == 4500)
    }

    @Test
    func anUnrecognisedCodeIsPreservedVerbatim() {
        #expect(GatewayCloseCode(rawValue: 4999) == .other(4999))
        #expect(GatewayCloseCode.other(4999).rawValue == 4999)
    }
}
