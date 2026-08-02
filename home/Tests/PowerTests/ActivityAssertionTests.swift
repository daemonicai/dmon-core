import Foundation
import Testing
@testable import Power

@Suite
struct ActivityAssertionTests {
    @Test
    func beginHoldsAndReleaseClearsTheAssertion() async {
        let assertion = ActivityAssertion(options: [.userInitiated], reason: "test")
        #expect(await assertion.isHeld == false)

        await assertion.begin()
        #expect(await assertion.isHeld == true)

        await assertion.release()
        #expect(await assertion.isHeld == false)
    }

    @Test
    func releaseIsSafeWhenNotHeld() async {
        let assertion = ActivityAssertion(options: [.userInitiated], reason: "test")

        await assertion.release()
        #expect(await assertion.isHeld == false)
    }

    @Test
    func doubleReleaseIsSafe() async {
        let assertion = ActivityAssertion(options: [.userInitiated], reason: "test")

        await assertion.begin()
        await assertion.release()
        await assertion.release()
        #expect(await assertion.isHeld == false)
    }

    @Test
    func beginTwiceKeepsTheAssertionHeldOnce() async {
        let assertion = ActivityAssertion(options: [.userInitiated], reason: "test")

        await assertion.begin()
        await assertion.begin()
        #expect(await assertion.isHeld == true)

        await assertion.release()
        #expect(await assertion.isHeld == false)
    }
}
