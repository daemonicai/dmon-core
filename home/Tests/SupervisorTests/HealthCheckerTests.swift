import Foundation
import Testing
@testable import Supervisor

@Suite
struct HealthCheckerTests {
    private static let url = URL(string: "http://127.0.0.1:9999")!

    @Test
    func httpCheckIsHealthyWhenTheProbeRespondsReachable() async {
        let checker = HealthChecker(httpProbe: { _ in true })
        let health = await checker.check(.http(Self.url), timeout: 5)
        #expect(health == .healthy)
    }

    @Test
    func httpCheckIsUnhealthyWhenTheProbeRespondsUnreachable() async {
        let checker = HealthChecker(httpProbe: { _ in false })
        let health = await checker.check(.http(Self.url), timeout: 5)
        #expect(health == .unhealthy)
    }

    /// The scenario's exact wording: a check that does not complete within
    /// its timeout is recorded as failed. The injected probe never returns
    /// within the timeout (it sleeps far longer), so this can only pass if
    /// the timeout is enforced by the caller rather than delegated to the
    /// probe.
    @Test
    func aHungHttpCheckIsRecordedAsUnhealthyRatherThanHanging() async {
        let checker = HealthChecker(httpProbe: { _ in
            try? await Task.sleep(nanoseconds: 3_600_000_000_000) // 1 hour: never elapses in-test
            return true
        })

        let clock = ContinuousClock()
        let start = clock.now
        let health = await checker.check(.http(Self.url), timeout: 0.05)
        let elapsed = start.duration(to: clock.now)

        #expect(health == .unhealthy)
        #expect(elapsed < .seconds(2))
    }

    @Test
    func tcpCheckReportsUnknownRegardlessOfOutcome() async {
        let checker = HealthChecker()
        let health = await checker.check(.tcp(host: "127.0.0.1", port: 8800), timeout: 5)
        #expect(health == .unknown)
    }

    @Test
    func processCheckReportsUnknownRegardlessOfOutcome() async {
        let checker = HealthChecker()
        let health = await checker.check(.process(executablePath: "tailscale", arguments: ["status"]), timeout: 5)
        #expect(health == .unknown)
    }

    @Test
    func noCheckReportsUnknown() async {
        let checker = HealthChecker()
        let health = await checker.check(.none, timeout: 5)
        #expect(health == .unknown)
    }
}
