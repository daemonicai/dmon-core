import Foundation
import Testing
@testable import Supervisor

@Suite
struct HealthMonitorTests {
    /// The scenario's other half: a hung check must not block *other*
    /// children. One descriptor's probe hangs well past its own timeout; two
    /// others take real, non-trivial latency (not "instant") before
    /// resolving. Serially this would take at least the hung check's own
    /// bound plus both healthy latencies — concurrently it takes close to
    /// the slowest single check. The asserted bound sits strictly between
    /// the two, so a regression to sequential fan-out fails this test rather
    /// than merely running slower than expected.
    @Test
    func aHungCheckPublishesFailureWithoutBlockingOtherChildren() async {
        let checker = HealthChecker(httpProbe: { url in
            if url.absoluteString.contains("hangs") {
                try? await Task.sleep(nanoseconds: 3_600_000_000_000)
                return true
            }
            try? await Task.sleep(nanoseconds: 150_000_000) // 150ms of real latency
            return true
        })
        let store = ChildHealthStore()
        let monitor = HealthMonitor(checker: checker, store: store)

        let entities: [any HealthCheckable] = [
            ChildDescriptor.stub(id: "hangs", timeout: 0.05),
            ChildDescriptor.stub(id: "healthy-a", timeout: 5),
            ChildDescriptor.stub(id: "healthy-b", timeout: 5)
        ]

        let clock = ContinuousClock()
        let start = clock.now
        await monitor.checkOnce(entities)
        let elapsed = start.duration(to: clock.now)

        // Serial: >= 0.05s (hung check's own bound) + 0.15s + 0.15s = 0.35s.
        // Concurrent: close to the slowest single check, ~0.15s.
        #expect(elapsed < .milliseconds(300))
        #expect(await store.health(for: "hangs") == .unhealthy)
        #expect(await store.health(for: "healthy-a") == .healthy)
        #expect(await store.health(for: "healthy-b") == .healthy)
    }

    /// The shared surface iterates both descriptor kinds without widening to
    /// `launch` or `adoptionPolicy` — this only compiles if `HealthCheckable`
    /// stayed narrow.
    @Test
    func checksBothDescriptorKindsThroughTheSharedSurface() async {
        let checker = HealthChecker(httpProbe: { _ in true })
        let store = ChildHealthStore()
        let monitor = HealthMonitor(checker: checker, store: store)

        let entities: [any HealthCheckable] = [
            ChildDescriptor.stub(id: "child", timeout: 5),
            MonitorDescriptor(
                id: "monitor",
                displayName: "Monitor",
                healthCheck: .http(URL(string: "http://127.0.0.1:9999")!),
                healthCheckTimeout: 5
            )
        ]

        await monitor.checkOnce(entities)

        #expect(await store.health(for: "child") == .healthy)
        #expect(await store.health(for: "monitor") == .healthy)
    }

    /// `run` must repeat `checkOnce` on its own, and must actually stop once
    /// cancelled rather than leaking a loop. `interval` is set to a
    /// near-zero value so the test does not wait on wall-clock time: pulling
    /// several elements from the store's update stream without the iterator
    /// hanging is itself the proof that `checkOnce` ran more than once.
    @Test
    func runRepeatsCheckOnceAndStopsOnCancellation() async {
        let checker = HealthChecker(httpProbe: { _ in true })
        let store = ChildHealthStore()
        let monitor = HealthMonitor(checker: checker, store: store, interval: 0.001)
        let entities: [any HealthCheckable] = [ChildDescriptor.stub(id: "child", timeout: 5)]

        let task = Task { await monitor.run(entities) }

        var iterator = await store.updates().makeAsyncIterator()
        _ = await iterator.next()
        _ = await iterator.next()
        _ = await iterator.next()

        task.cancel()

        // A plain await, not a bounded one: `task.value` is not itself a
        // cancellation checkpoint, so this only resolves once `run`'s loop
        // notices cancellation (via `Task.sleep` throwing) and returns. If
        // that cooperation ever regresses, this test hangs rather than
        // failing fast — there is no way to bound an await on a task's
        // completion without falling into the same trap `withTimeout` itself
        // exists to avoid.
        await task.value
    }
}

extension ChildDescriptor {
    fileprivate static func stub(id: ChildID, timeout: TimeInterval) -> ChildDescriptor {
        let url = URL(string: "http://127.0.0.1:9999/\(id.rawValue)")!
        return ChildDescriptor(
            id: id,
            displayName: id.rawValue,
            transport: .loopbackHTTP,
            endpoint: url,
            healthCheck: .http(url),
            healthCheckTimeout: timeout,
            startupOrder: 0,
            adoptionPolicy: .adoptOrSpawn,
            launch: ChildLaunch(),
            isEnabled: false
        )
    }
}
