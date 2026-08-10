import Foundation
import Testing
@testable import Supervisor
#if canImport(Darwin)
import Darwin
#endif

@Suite
struct ChildStartCoordinatorTests {
    private static let endpoint = URL(string: "http://127.0.0.1:9999/child")!

    /// The scenario's exact wording: a live child is adopted and not spawned
    /// a second time. Real, resolvable launch candidates are supplied
    /// alongside the healthy probe — if adoption did not genuinely
    /// short-circuit before the spawn path, this would return `.spawned`
    /// instead of `.adopted`, which is exactly the distinction the brief
    /// asked to be provable.
    @Test
    func adoptOrSpawnAdoptsALiveChildWithoutSpawning() async {
        let descriptor = Self.descriptor(policy: .adoptOrSpawn, candidates: [.absolutePath("/bin/sh")])
        let coordinator = ChildStartCoordinator(
            healthChecker: HealthChecker(httpProbe: { url in url == Self.endpoint }),
            resolver: ExecutableResolver(),
            spawner: ChildSpawner()
        )

        let outcome = await coordinator.start(descriptor)

        guard case .adopted = outcome else {
            Issue.record("expected .adopted, got \(outcome)")
            return
        }
    }

    /// The scenario's other half: nothing answers, so the coordinator
    /// spawns a real process rather than reporting success without one.
    @Test
    func adoptOrSpawnSpawnsWhenNothingAnswers() async throws {
        let descriptor = Self.descriptor(policy: .adoptOrSpawn, candidates: [.absolutePath("/bin/sh")])
        let coordinator = ChildStartCoordinator(
            healthChecker: HealthChecker(httpProbe: { _ in false }),
            resolver: ExecutableResolver(),
            spawner: ChildSpawner()
        )

        let outcome = await coordinator.start(descriptor)

        guard case .spawned(let child) = outcome else {
            Issue.record("expected .spawned, got \(outcome)")
            return
        }
        #expect(kill(child.pid, 0) == 0, "spawned child should be a real, live process")
        #expect(getpgid(child.pid) != getpgrp())

        #expect(ChildSpawner().killProcessGroup(of: child))
        _ = await awaitExit(of: child.pid)
    }

    /// `.spawnOnly` must never adopt, even when the endpoint answers —
    /// otherwise the two policies would be indistinguishable whenever a
    /// child happens to already be up.
    @Test
    func spawnOnlyNeverAdoptsEvenWhenTheEndpointAnswers() async throws {
        let descriptor = Self.descriptor(policy: .spawnOnly, candidates: [.absolutePath("/bin/sh")])
        let coordinator = ChildStartCoordinator(
            healthChecker: HealthChecker(httpProbe: { _ in true }),
            resolver: ExecutableResolver(),
            spawner: ChildSpawner()
        )

        let outcome = await coordinator.start(descriptor)

        guard case .spawned(let child) = outcome else {
            Issue.record("expected .spawned, got \(outcome)")
            return
        }
        #expect(ChildSpawner().killProcessGroup(of: child))
        _ = await awaitExit(of: child.pid)
    }

    @Test
    func spawnOnlyNeverInvokesTheHealthCheckAtAll() async throws {
        let descriptor = Self.descriptor(policy: .spawnOnly, candidates: [.absolutePath("/bin/sh")])
        let coordinator = ChildStartCoordinator(
            healthChecker: HealthChecker(httpProbe: { _ in
                Issue.record("spawnOnly must not consult the health check")
                return true
            }),
            resolver: ExecutableResolver(),
            spawner: ChildSpawner()
        )

        let outcome = await coordinator.start(descriptor)
        guard case .spawned(let child) = outcome else {
            Issue.record("expected .spawned, got \(outcome)")
            return
        }
        #expect(ChildSpawner().killProcessGroup(of: child))
        _ = await awaitExit(of: child.pid)
    }

    @Test
    func emptyLaunchCandidatesAreNotDecidedRatherThanAFailure() async {
        let descriptor = Self.descriptor(policy: .adoptOrSpawn, candidates: [])
        let coordinator = ChildStartCoordinator(
            healthChecker: HealthChecker(httpProbe: { _ in false }),
            resolver: ExecutableResolver(),
            spawner: ChildSpawner()
        )

        let outcome = await coordinator.start(descriptor)
        guard case .launchNotDecided = outcome else {
            Issue.record("expected .launchNotDecided, got \(outcome)")
            return
        }
    }

    /// Distinct from the case above: candidates were declared, they just
    /// didn't resolve to anything real. Collapsing this into
    /// `.launchNotDecided` would hide a genuine misconfiguration behind a
    /// case that means "not decided yet".
    @Test
    func candidatesThatResolveToNothingAreExecutableNotResolved() async {
        let descriptor = Self.descriptor(policy: .adoptOrSpawn, candidates: [.absolutePath("/nowhere/at/all")])
        let coordinator = ChildStartCoordinator(
            healthChecker: HealthChecker(httpProbe: { _ in false }),
            resolver: ExecutableResolver(),
            spawner: ChildSpawner()
        )

        let outcome = await coordinator.start(descriptor)
        guard case .executableNotResolved = outcome else {
            Issue.record("expected .executableNotResolved, got \(outcome)")
            return
        }
    }

    private static func descriptor(policy: AdoptionPolicy, candidates: [ExecutableSource]) -> ChildDescriptor {
        ChildDescriptor(
            id: "test-child",
            displayName: "Test Child",
            transport: .loopbackHTTP,
            endpoint: endpoint,
            healthCheck: .http(endpoint),
            healthCheckTimeout: 5,
            startupOrder: 0,
            adoptionPolicy: policy,
            launch: ChildLaunch(candidates: candidates),
            isEnabled: true
        )
    }
}
