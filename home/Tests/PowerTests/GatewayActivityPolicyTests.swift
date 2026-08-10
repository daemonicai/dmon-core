import Foundation
import Testing
@testable import Power

/// Both scenarios of "The host holds an activity assertion while the
/// gateway is enabled" (spec), plus idempotence — `apply` is expected to be
/// safe to call repeatedly, including with an unchanged value.
@Suite
struct GatewayActivityPolicyTests {
    @Test
    func applyingTrueHoldsTheAssertion() async {
        let policy = GatewayActivityPolicy(reason: "test")
        #expect(await policy.isHolding == false)

        await policy.apply(gatewayEnabled: true)

        #expect(await policy.isHolding == true)
    }

    @Test
    func applyingFalseReleasesTheAssertion() async {
        let policy = GatewayActivityPolicy(reason: "test")
        await policy.apply(gatewayEnabled: true)
        #expect(await policy.isHolding == true)

        await policy.apply(gatewayEnabled: false)

        #expect(await policy.isHolding == false)
    }

    @Test
    func applyingTrueRepeatedlyKeepsTheAssertionHeldOnce() async {
        let policy = GatewayActivityPolicy(reason: "test")

        await policy.apply(gatewayEnabled: true)
        await policy.apply(gatewayEnabled: true)

        #expect(await policy.isHolding == true)
    }

    @Test
    func applyingFalseWhenNotHoldingIsSafe() async {
        let policy = GatewayActivityPolicy(reason: "test")

        await policy.apply(gatewayEnabled: false)

        #expect(await policy.isHolding == false)
    }
}
