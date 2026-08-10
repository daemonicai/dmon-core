import Testing
@testable import Supervisor

@Suite
struct ChildHealthTests {
    @Test
    func allCasesRoundTripThroughRawValue() {
        let cases: [ChildHealth] = [.unknown, .healthy, .unhealthy]
        for health in cases {
            #expect(ChildHealth(rawValue: health.rawValue) == health)
        }
    }
}
