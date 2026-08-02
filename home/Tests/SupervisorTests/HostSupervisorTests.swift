import Foundation
import Testing
@testable import Supervisor
#if canImport(Darwin)
import Darwin
#endif

@Suite
struct HostSupervisorTests {

    // MARK: - Crash detection with backoff

    /// The brief's own example: a child that launches fine and dies
    /// immediately, repeatedly. Proves the *sequence* of requested delays —
    /// via an injected sleep that records rather than waits — not merely
    /// that "some delay grew".
    ///
    /// The script crashes for its first six invocations (each recorded via a
    /// counter file) and then sleeps instead, so the loop has somewhere to
    /// stop once the six delays this test needs have been requested — with
    /// the injected sleep being instant, nothing else would ever throttle it.
    @Test
    func repeatedImmediateCrashesRequestAnEscalatingCappedDelaySequence() async throws {
        let counterFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: counterFile) }
        try "0".write(to: counterFile, atomically: true, encoding: .utf8)

        let script = """
        n=$(( $(cat "\(counterFile.path)") + 1 ))
        echo "$n" > "\(counterFile.path)"
        if [ "$n" -le 6 ]; then
          exit 1
        fi
        sleep 30
        """
        let descriptor = Self.descriptor(id: "crash-loop", command: script)
        let recorder = DelayRecorder()
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            store: ChildSupervisionStore(),
            sleep: { delay in await recorder.record(delay) }
        )

        await supervisor.start()
        let sawSixCrashes = await waitUntilTrue(timeout: 5) { await recorder.count >= 6 }
        #expect(sawSixCrashes, "expected 6 recorded restart delays")
        #expect(await recorder.delays == [2, 4, 8, 16, 32, 60])

        await supervisor.shutdown()
    }

    /// The interaction B4 exists for: without an explicit intentional-stop
    /// path, quitting the host would restart everything it just shut down.
    /// This distinguishes "restarted" from "never stopped" by asserting the
    /// exact marker sequence: a second `spawned` line would appear if the
    /// termination were ever mistaken for a crash.
    @Test
    func gracefulShutdownTriggersNoRestart() async throws {
        let markerFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: markerFile) }

        let script = """
        echo spawned >> "\(markerFile.path)"
        trap 'echo terminated >> "\(markerFile.path)"; exit 0' TERM
        sleep 30 &
        wait
        """
        let descriptor = Self.descriptor(id: "graceful-child", command: script)
        let supervisor = HostSupervisor(descriptors: [descriptor], store: ChildSupervisionStore(), gracefulShutdownTimeout: 3)

        await supervisor.start()
        let sawSpawn = await waitUntilTrue(timeout: 2) {
            (try? String(contentsOf: markerFile, encoding: .utf8))?.contains("spawned") == true
        }
        #expect(sawSpawn)

        await supervisor.shutdown()

        let lines = ((try? String(contentsOf: markerFile, encoding: .utf8)) ?? "")
            .split(separator: "\n").map(String.init)
        #expect(lines == ["spawned", "terminated"], "no second spawn should follow the intentional termination")

        // Also assert the *terminal state*, not just the absence of a
        // restart: a variant that skips straight to re-checking
        // `intentionalStop` only after wastefully computing and sleeping out
        // a backoff delay would still not restart (this same assertion above
        // would still pass), but would leave the store showing
        // `.restarting`/`.repeatedFailure` rather than `.stoppedIntentionally`.
        let finalState = await supervisor.currentSupervisionState(for: descriptor.id)
        #expect(finalState == .stoppedIntentionally)
    }

    // MARK: - Startup and shutdown order

    /// Three children, and — this is the point — passed to the constructor
    /// in an order that does **not** match declared `startupOrder`
    /// (`third`, `first`, `second`, declared orders 2/0/1). Two children
    /// whose insertion order already matches ascending `startupOrder` cannot
    /// tell "sorted by declared order" apart from "insertion order preserved
    /// verbatim" — deleting `HostSupervisor.init`'s `.sorted` would still
    /// pass that version of this test identically.
    @Test
    func shutdownTerminatesChildrenInReverseOfStartupOrder() async throws {
        let logFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let readyFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer {
            try? FileManager.default.removeItem(at: logFile)
            try? FileManager.default.removeItem(at: readyFile)
        }

        // Each child announces "ready" only once its `trap` is installed, so
        // the test never signals a child before it can possibly react —
        // that race (not the host) is what an earlier round of this test
        // fell into.
        func script(marker: String) -> String {
            """
            trap 'echo \(marker) >> "\(logFile.path)"; exit 0' TERM
            echo \(marker) >> "\(readyFile.path)"
            sleep 30 &
            wait
            """
        }
        let first = Self.descriptor(id: "first", startupOrder: 0, command: script(marker: "first"))
        let second = Self.descriptor(id: "second", startupOrder: 1, command: script(marker: "second"))
        let third = Self.descriptor(id: "third", startupOrder: 2, command: script(marker: "third"))
        let supervisor = HostSupervisor(
            descriptors: [third, first, second],
            store: ChildSupervisionStore(),
            gracefulShutdownTimeout: 3
        )

        await supervisor.start()
        let allReady = await waitUntilTrue(timeout: 2) {
            let ready = (try? String(contentsOf: readyFile, encoding: .utf8)) ?? ""
            return ready.contains("first") && ready.contains("second") && ready.contains("third")
        }
        #expect(allReady)

        await supervisor.shutdown()

        let lines = ((try? String(contentsOf: logFile, encoding: .utf8)) ?? "")
            .split(separator: "\n").map(String.init)
        #expect(lines == ["third", "second", "first"], "children must be terminated in the reverse of their declared startup order")
    }

    /// The trap only fires if `SIGTERM` (not `SIGKILL`) arrived first: if the
    /// host escalated straight to `SIGKILL`, this file would stay empty.
    @Test
    func eachChildIsAskedToTerminateGracefullyBeforeBeingKilled() async throws {
        let markerFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: markerFile) }

        // The child announces readiness only after its `trap` is installed,
        // so `shutdown()` below can never race ahead of it.
        let script = """
        trap 'echo graceful >> "\(markerFile.path)"; exit 0' TERM
        echo ready >> "\(markerFile.path)"
        sleep 30 &
        wait
        """
        let descriptor = Self.descriptor(id: "polite-child", command: script)
        let supervisor = HostSupervisor(descriptors: [descriptor], store: ChildSupervisionStore(), gracefulShutdownTimeout: 3)

        await supervisor.start()
        let isReady = await waitUntilTrue(timeout: 2) {
            (try? String(contentsOf: markerFile, encoding: .utf8))?.contains("ready") == true
        }
        #expect(isReady)

        await supervisor.shutdown()

        let content = (try? String(contentsOf: markerFile, encoding: .utf8)) ?? ""
        #expect(content.contains("graceful"))
    }

    /// The escalation half: a child whose **whole process group** ignores
    /// `SIGTERM` must still end up dead once `shutdown()` returns.
    ///
    /// No backgrounding here, deliberately: `trap '' TERM` sets `SIGTERM`'s
    /// disposition to `SIG_IGN`, which survives `exec` — but only a
    /// *foreground* child (spawned and waited on directly by the shell, as
    /// `while true; do sleep 1; done` does) reliably inherits it. An earlier
    /// version of this test used `sleep 30 &` (backgrounded) instead, and
    /// that backgrounded `sleep` died on `SIGTERM`'s default action anyway —
    /// which let `wait` return, the script exit on its own, and the whole
    /// test complete in ~0.001s having exercised the *graceful* path, not
    /// escalation, despite asserting the escalation half.
    @Test
    func shutdownEscalatesToSigkillWhenGracefulTerminationIsIgnored() async throws {
        let readyFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: readyFile) }

        let script = """
        trap '' TERM
        echo ready >> "\(readyFile.path)"
        while true; do sleep 1; done
        """
        let descriptor = Self.descriptor(id: "stubborn-child", command: script)
        let supervisor = HostSupervisor(descriptors: [descriptor], store: ChildSupervisionStore(), gracefulShutdownTimeout: 0.3)

        await supervisor.start()
        let pid = try #require(await supervisor.currentPID(for: descriptor.id))
        let isReady = await waitUntilTrue(timeout: 2) {
            (try? String(contentsOf: readyFile, encoding: .utf8))?.contains("ready") == true
        }
        #expect(isReady)
        #expect(kill(pid, 0) == 0, "child should be alive before shutdown")

        await supervisor.shutdown()

        #expect(kill(pid, 0) != 0, "child should be dead after shutdown escalated to SIGKILL")
    }

    /// `.repeatedFailure` is driven by its own `repeatedFailureThreshold`,
    /// not by the backoff delay reaching `maximum` — proven by setting
    /// `maximum` far out of reach (1000s) while `repeatedFailureThreshold`
    /// is small (2). If the two were still coupled, nothing would surface
    /// `.repeatedFailure` here within the timeout: 1000s is nowhere near
    /// reachable from two crashes at a 10ms initial delay.
    ///
    /// The child crashes on every single invocation (never reaching a
    /// `sleep`-and-go-quiet branch) rather than going quiet after N
    /// invocations, so this keeps crashing well past the threshold — which
    /// is also what `repeatedFailureDoesNotFlickerBackToNormalBetweenRestartAttempts`
    /// below needs to observe.
    @Test
    func repeatedFailureSurfacesAtItsOwnThresholdIndependentOfTheBackoffCap() async throws {
        let descriptor = Self.descriptor(id: "threshold-child", command: "exit 1")
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            store: ChildSupervisionStore(),
            backoff: RestartBackoff(initial: 0.01, maximum: 1000, stabilityThreshold: 999),
            repeatedFailureThreshold: 2
        )

        await supervisor.start()
        let sawRepeatedFailure = await waitUntilTrue(timeout: 3) {
            if case .repeatedFailure = await supervisor.currentSupervisionState(for: descriptor.id) {
                return true
            }
            return false
        }
        #expect(sawRepeatedFailure, "two unstable crashes should surface repeated failure even though the backoff delay is nowhere near its 1000s cap")

        await supervisor.shutdown()
    }

    /// Surfacing repeated failure is only meaningful if it *stays* surfaced.
    /// Each restart attempt re-spawns the child (a real, if short-lived,
    /// process) and `apply`'s `.spawned` case has its own reason to publish
    /// — the naive version of that fix always published `.normal` there,
    /// which meant the state cycled `.repeatedFailure` → `.normal` →
    /// `.repeatedFailure` on every single iteration of an ongoing crash
    /// loop. This polls the *raw* published state (not just "was
    /// `.repeatedFailure` ever seen", which the test above already covers)
    /// across many restart attempts and asserts `.normal` never reappears
    /// once the streak has been surfaced.
    @Test
    func repeatedFailureDoesNotFlickerBackToNormalBetweenRestartAttempts() async throws {
        let descriptor = Self.descriptor(id: "flicker-child", command: "exit 1")
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            store: ChildSupervisionStore(),
            backoff: RestartBackoff(initial: 0.02, maximum: 1000, stabilityThreshold: 999),
            repeatedFailureThreshold: 2
        )

        await supervisor.start()

        // Breaks out once the invariant has had a solid run of consecutive
        // confirmations, rather than always sampling to the end of a fixed
        // window: `consecutiveUnstableCrashes` is monotonically
        // non-decreasing for an always-crashing child with
        // `stabilityThreshold` this far out of reach, so once
        // `.repeatedFailure` has held for many samples in a row there is
        // nothing further polling could learn — it cannot un-flicker later
        // if it hasn't already. The `Issue.record` below (for the window
        // elapsing before even the *first* `.repeatedFailure`) still applies
        // unchanged — this only shortens the happy path.
        var observed: [ChildSupervisionState] = []
        var consecutiveRepeatedFailureSamples = 0
        let requiredConsecutiveSamples = 20
        let deadline = Date().addingTimeInterval(0.6)
        while Date() < deadline {
            let state = await supervisor.currentSupervisionState(for: descriptor.id)
            observed.append(state)
            if case .repeatedFailure = state {
                consecutiveRepeatedFailureSamples += 1
                if consecutiveRepeatedFailureSamples >= requiredConsecutiveSamples {
                    break
                }
            } else {
                consecutiveRepeatedFailureSamples = 0
            }
            try? await Task.sleep(nanoseconds: 2_000_000)
        }

        await supervisor.shutdown()

        guard let firstRepeatedFailureIndex = observed.firstIndex(where: {
            if case .repeatedFailure = $0 { return true }
            return false
        }) else {
            Issue.record("expected to observe .repeatedFailure at least once; saw \(observed)")
            return
        }
        let flickeredBackToNormal = observed[firstRepeatedFailureIndex...].contains {
            if case .normal = $0 { return true }
            return false
        }
        #expect(!flickeredBackToNormal, "state flickered back to .normal after first surfacing .repeatedFailure: \(observed)")
    }

    // MARK: - Restart goes back through ChildStartCoordinator

    /// If the endpoint has been taken over by the time a crashed child would
    /// restart, the correct behaviour is to adopt it, not spawn a second
    /// process underneath it. The probe answers `false` on its first call
    /// (so the initial start spawns) and `true` thereafter (simulating
    /// something else having taken the endpoint by the time the restart
    /// runs).
    @Test
    func aRestartAdoptsIfTheEndpointAnswersByTheTimeItRuns() async throws {
        let endpoint = URL(string: "http://127.0.0.1:9321/restart-adopt")!
        let probeCallCount = ProbeCallCount()
        let coordinator = ChildStartCoordinator(
            healthChecker: HealthChecker(httpProbe: { _ in
                await probeCallCount.increment() > 1
            })
        )
        let descriptor = Self.descriptor(
            id: "adopt-after-restart",
            policy: .adoptOrSpawn,
            endpoint: endpoint,
            command: "exit 1"
        )
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            coordinator: coordinator,
            store: ChildSupervisionStore(),
            backoff: RestartBackoff(initial: 0.05, maximum: 0.05, stabilityThreshold: 999)
        )

        await supervisor.start()
        let initialPid = await supervisor.currentPID(for: descriptor.id)
        #expect(initialPid != nil, "first attempt should spawn, since nothing answers yet")

        let adopted = await waitUntilTrue(timeout: 3) {
            await supervisor.currentPID(for: descriptor.id) == nil
        }
        #expect(adopted, "the restart should adopt rather than spawn a second process")
    }

    // MARK: - Helpers

    private static func descriptor(
        id: ChildID,
        startupOrder: Int = 0,
        policy: AdoptionPolicy = .spawnOnly,
        endpoint: URL = URL(string: "http://127.0.0.1:9999/unused")!,
        command: String
    ) -> ChildDescriptor {
        ChildDescriptor(
            id: id,
            displayName: id.rawValue,
            transport: .loopbackHTTP,
            endpoint: endpoint,
            healthCheck: .http(endpoint),
            healthCheckTimeout: 1,
            startupOrder: startupOrder,
            adoptionPolicy: policy,
            launch: ChildLaunch(candidates: [.absolutePath("/bin/sh")], arguments: ["-c", command]),
            isEnabled: true
        )
    }
}

private actor DelayRecorder {
    private(set) var delays: [TimeInterval] = []
    var count: Int { delays.count }
    func record(_ delay: TimeInterval) {
        delays.append(delay)
    }
}

private actor ProbeCallCount {
    private var count = 0
    func increment() -> Int {
        count += 1
        return count
    }
}

