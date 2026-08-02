import Testing
@testable import Supervisor

@Suite
struct ChildHealthTests {
    @Test
    func allCasesRoundTripThroughRawValue() {
        let cases: [ChildHealth] = [.unknown, .starting, .healthy, .unhealthy, .stopped]
        for health in cases {
            #expect(ChildHealth(rawValue: health.rawValue) == health)
        }
    }

    @Test
    func runningStatesReportIsRunning() {
        #expect(ChildHealth.starting.isRunning)
        #expect(ChildHealth.healthy.isRunning)
        #expect(ChildHealth.unhealthy.isRunning)
    }

    @Test
    func nonRunningStatesReportNotRunning() {
        #expect(!ChildHealth.unknown.isRunning)
        #expect(!ChildHealth.stopped.isRunning)
    }
}
