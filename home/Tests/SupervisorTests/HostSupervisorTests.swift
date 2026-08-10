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

        _ = await supervisor.shutdown()
    }

    /// The consequence, not the symptom the test above already covers.
    /// `apply`/`handleExit`/`shutdownChild` used to capture `states[id]`
    /// into a local `ChildState`, mutate the copy, and write the whole
    /// thing back after an `await` — see `apply`'s own doc comment for the
    /// failure this caused: a generation that crashed and respawned while
    /// an *earlier* generation's own `handleExit` was still suspended
    /// (reentrant, since this actor is reentrant at every `await`) could
    /// have its freshly-recorded `SpawnedChild` silently overwritten when
    /// the earlier call resumed and wrote its stale copy back — leaving the
    /// supervisor tracking a dead pid while the real, newest generation
    /// kept running with no reference to it anywhere. `shutdownChild` would
    /// then find and signal the wrong (already-dead) pid, and the live one
    /// would survive host shutdown entirely, re-parented to launchd —
    /// requirement 5 (design D6) failing silently for a process this host
    /// itself spawned. A fix that happened to get the *delay numbers*
    /// right (the test above) without actually closing this would still
    /// fail this one: it asserts the supervisor ends the storm tracking the
    /// *actual* newest generation's pid, independently confirmed via a
    /// marker file the surviving generation writes itself, and that
    /// `shutdown()` genuinely signals that exact pid.
    ///
    /// **What this test actually is, stated plainly.** This is a
    /// correctness/invariant check against the *fixed* code — it asserts
    /// that after a crash-restart storm, tracking is consistent with
    /// reality (the tracked pid is the newest generation's, and shutdown
    /// signals it) — **not a regression guard for the specific reentrancy
    /// race** `apply`'s doc comment describes. That race is a single
    /// child's very first spawn racing its own near-instant crash against
    /// `apply`'s own `await store.publish(.normal, for: id)`, and
    /// `store.publish` is a trivial, same-process actor hop — fast enough
    /// that a child's real crash-detection (kernel process-exit delivery,
    /// categorically slower) does not appear to win that race in practice
    /// on this platform: twenty solo repetitions of this scenario against
    /// the *reverted*, buggy code (`state: inout ChildState`, written back
    /// after the `await`) passed all twenty times, and this test's own
    /// 40-concurrent-children shape, run against that same reverted code,
    /// **also produced zero failures** — concurrent scheduling pressure
    /// from many simultaneous storms did not change the outcome. The
    /// defect the reverted code has is real (verified statically, and by
    /// the reviewer independently), but neither shape above catches it
    /// empirically. What running many children *does* still buy: were
    /// `apply` to regress in some coarser way — losing track of which pid
    /// is current at all, for reasons unrelated to this specific timing
    /// window — checking every one of forty independent storms rather than
    /// one makes that far less likely to pass by accident. Kept at 40
    /// children for that reason, not because the count improves this
    /// test's odds against the narrow race it was originally written to
    /// catch.
    @Test
    func aCrashRestartStormEndsWithTheSupervisorTrackingTheNewestGenerationNotAStaleOne() async throws {
        let childCount = 40
        var descriptors: [ChildDescriptor] = []
        var counterFiles: [ChildID: URL] = [:]
        var pidFiles: [ChildID: URL] = [:]

        for i in 0..<childCount {
            let id = ChildID("storm-child-\(i)")
            let counterFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            let pidFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try "0".write(to: counterFile, atomically: true, encoding: .utf8)
            counterFiles[id] = counterFile
            pidFiles[id] = pidFile

            // Six near-instant crashes, then a stable seventh generation
            // that announces its own real pid independently, via a marker
            // file rather than anything this test could compute or infer
            // from `HostSupervisor`'s own bookkeeping — so
            // `trackedPID == survivingPID` below is a comparison against
            // ground truth, not against another view of the same
            // possibly-corrupted state.
            let script = """
            n=$(( $(cat "\(counterFile.path)") + 1 ))
            echo "$n" > "\(counterFile.path)"
            if [ "$n" -le 6 ]; then
              exit 1
            fi
            echo $$ > "\(pidFile.path)"
            sleep 30
            """
            descriptors.append(Self.descriptor(id: id, startupOrder: i, command: script))
        }
        defer {
            for url in counterFiles.values { try? FileManager.default.removeItem(at: url) }
            for url in pidFiles.values { try? FileManager.default.removeItem(at: url) }
        }

        let supervisor = HostSupervisor(
            descriptors: descriptors,
            store: ChildSupervisionStore(),
            // Bounds `shutdown()`'s worst case: it walks all `childCount`
            // children strictly sequentially, and this test's own script
            // has no `TERM` trap (default disposition kills it instantly),
            // so a generous-but-small per-child budget still leaves ample
            // room while keeping a slow run bounded rather than potentially
            // compounding to `childCount * gracefulShutdownTimeout` if a
            // handful of real spawns are ever sluggish to reap under load.
            gracefulShutdownTimeout: 1,
            // An instant, non-blocking injected sleep, for the same reason
            // as the canary test above: real backoff delays make the
            // reentrancy window vanishingly unlikely to matter, this makes
            // it reachable.
            sleep: { _ in }
        )

        await supervisor.start()

        var survivingPIDs: [ChildID: pid_t] = [:]
        for descriptor in descriptors {
            survivingPIDs[descriptor.id] = try await Self.waitForPid(at: pidFiles[descriptor.id]!, timeout: 10)
        }

        for descriptor in descriptors {
            let survivingPID = survivingPIDs[descriptor.id]!
            let trackedPID = await supervisor.currentPID(for: descriptor.id)
            #expect(
                trackedPID == survivingPID,
                "\(descriptor.id): the supervisor must track the newest generation's own pid (\(survivingPID)), not an earlier, already-dead generation's (got \(String(describing: trackedPID)))"
            )
            #expect(kill(survivingPID, 0) == 0, "\(descriptor.id): the newest generation should still be alive before shutdown")
        }

        _ = await supervisor.shutdown()

        for descriptor in descriptors {
            let survivingPID = survivingPIDs[descriptor.id]!
            let died = await waitUntilTrue(timeout: 2) { kill(survivingPID, 0) != 0 }
            #expect(died, "\(descriptor.id): shutdown must signal the actual newest generation's pid, not a stale one")
        }
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

        // Trap first, marker second, and the order is load-bearing: the
        // marker is this test's readiness signal, so it must not appear
        // until a `TERM` handler exists. Written the other way round —
        // as it was — `shutdown()`'s `SIGTERM` can land in the window
        // before the trap is installed, the shell's default disposition
        // kills the child outright, no `terminated` line is ever written,
        // and this test fails having proved nothing about restarts.
        // Measured at roughly 1 in 40 full-suite runs; injecting a sleep
        // between the two lines makes it fail 5/5, and the same injection
        // against this ordering passes 5/5.
        let script = """
        trap 'echo terminated >> "\(markerFile.path)"; exit 0' TERM
        echo spawned >> "\(markerFile.path)"
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

        _ = await supervisor.shutdown()

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

        _ = await supervisor.shutdown()

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

        _ = await supervisor.shutdown()

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

        _ = await supervisor.shutdown()

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

        _ = await supervisor.shutdown()
    }

    /// Surfacing repeated failure is only meaningful if it *stays* surfaced.
    /// Each restart attempt re-spawns the child (a real, if short-lived,
    /// process) and `apply`'s `.spawned` case has its own reason to publish
    /// — the naive version of that fix always published `.normal` there,
    /// which meant the state cycled `.repeatedFailure` → `.normal` →
    /// `.repeatedFailure` on every single iteration of an ongoing crash
    /// loop.
    ///
    /// This used to poll `currentSupervisionState` on a 2ms timer against a
    /// fixed 0.6s wall-clock window — sampling, not forcing: on a slow CI
    /// runner the loop could fail at its own guard (never observing even
    /// the *first* `.repeatedFailure` within the window) before ever
    /// exercising the flicker property, and even when it did keep up, a
    /// sample taken every 2ms can step clean over a state that is published
    /// and immediately overwritten between two ticks.
    ///
    /// Forces the mechanism instead, the same way
    /// `HealthMonitorTests.runRepeatsCheckOnceAndStopsOnCancellation` does
    /// for the sibling health-check store: `ChildSupervisionStore.updates()`
    /// is an unbounded-buffered `AsyncStream` that yields once per
    /// `publish(_:for:)` call, including a no-op republish — so consuming it
    /// misses no transition regardless of runner speed, unlike polling on an
    /// interval clock. `sleep: { _ in }` (the same injection point
    /// `repeatedImmediateCrashesRequestAnEscalatingCappedDelaySequence` and
    /// the crash-restart-storm test already use) makes each restart attempt
    /// run back-to-back rather than waiting out `RestartBackoff`'s real,
    /// exponentially growing delay — so a fixed, small number of restart
    /// cycles is reachable quickly on any runner, fast or slow.
    @Test
    func repeatedFailureDoesNotFlickerBackToNormalBetweenRestartAttempts() async throws {
        let descriptor = Self.descriptor(id: "flicker-child", command: "exit 1")
        let store = ChildSupervisionStore()
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            store: store,
            backoff: RestartBackoff(initial: 0.02, maximum: 1000, stabilityThreshold: 999),
            repeatedFailureThreshold: 2,
            sleep: { _ in }
        )

        // Subscribed before `start()`, so the unbounded buffer holds every
        // publish from this child's very first start onward — nothing
        // between subscribing and the first `iterator.next()` call below can
        // be missed.
        let updates = await store.updates()
        await supervisor.start()

        // Drives a definite number of restart *cycles* (real published
        // transitions), not a sampling count: the loop only advances on an
        // actual `publish(_:for:)` call, so it cannot finish early having
        // observed nothing, and cannot skip a transition that occurred
        // between two iterations — there is no "between", every one is
        // delivered. `withTimeout` here is purely a hang guard against a
        // genuine production regression (e.g. restarts stopping
        // altogether), not a substitute for the property assertion below.
        let observed: [ChildSupervisionState]? = await withTimeout(10) {
            var iterator = updates.makeAsyncIterator()
            var states: [ChildSupervisionState] = []
            var repeatedFailureCount = 0
            while repeatedFailureCount < 15 {
                guard let snapshot = await iterator.next() else { break }
                guard let state = snapshot[descriptor.id] else { continue }
                states.append(state)
                if case .repeatedFailure = state {
                    repeatedFailureCount += 1
                }
            }
            return states
        }

        _ = await supervisor.shutdown()

        guard let observed else {
            Issue.record("timed out waiting for 15 .repeatedFailure cycles")
            return
        }
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

    // MARK: - Process-group kill and adoption exemption on shutdown

    /// "Adoption is exempt" (task 4.6), proven against the ordinary
    /// `shutdown()` path now that the standalone process-group sweep this
    /// used to be proven through has folded into it (DEVLOG remediation for
    /// task 4.7, R1/R6): an adopted descriptor never populates
    /// `spawnedChild` in the first place (`apply`'s `.adopted` case), so
    /// there is structurally nothing for `shutdownChild` to signal.
    /// `currentPID(for:) == nil`, both before and after `shutdown()`, is
    /// the only claim any implementation could ever prove through this
    /// type: an adopted child's real backing pid is never communicated to
    /// `HostSupervisor` in any form, so no test double standing in for
    /// "the adopted process" can be observed alive or dead through this
    /// API — asserting on one, as an earlier version of this test did,
    /// proves nothing an implementation could ever falsify. Proven
    /// alongside a genuinely spawned sibling that *is* killed, so this is
    /// not merely "shutdown does nothing".
    @Test
    func shutdownExemptsAnAdoptedChildAndKillsAGenuinelySpawnedSibling() async throws {
        let coordinator = ChildStartCoordinator(healthChecker: HealthChecker(httpProbe: { _ in true }))
        let adoptedDescriptor = Self.descriptor(id: "adopted-child", policy: .adoptOrSpawn, command: "sleep 30")
        let spawnedDescriptor = Self.descriptor(id: "genuinely-spawned", command: "sleep 30")
        let supervisor = HostSupervisor(
            descriptors: [adoptedDescriptor, spawnedDescriptor],
            coordinator: coordinator,
            store: ChildSupervisionStore()
        )

        await supervisor.start()
        #expect(
            await supervisor.currentPID(for: adoptedDescriptor.id) == nil,
            "an adoptOrSpawn descriptor whose endpoint already answers should adopt, not spawn"
        )
        let spawnedPid = try #require(await supervisor.currentPID(for: spawnedDescriptor.id))

        let refused = await supervisor.shutdown()

        #expect(refused.isEmpty)
        let spawnedDied = await waitUntilTrue(timeout: 2) { kill(spawnedPid, 0) != 0 }
        #expect(spawnedDied, "the genuinely spawned sibling should be killed by shutdown")
        #expect(
            await supervisor.currentPID(for: adoptedDescriptor.id) == nil,
            "an adopted child has no pid for shutdown to ever have touched"
        )
    }

    // MARK: - Worst-case shutdown duration

    /// The one fact an app-exit termination budget must derive from (task
    /// 4.7's remediation, R5): a plain product of `gracefulShutdownTimeout`
    /// and how many children `shutdown()` actually walks — proven here by
    /// choosing values (7s, 3 children) that could not coincide with any
    /// other plausible formula (e.g. summing instead of multiplying, or
    /// off-by-one on the count) without failing the exact-equality check.
    /// `nonisolated`, so no `await` needed to read it.
    @Test
    func worstCaseShutdownDurationIsGracefulTimeoutTimesEnabledChildCount() {
        let descriptors = (0..<3).map {
            Self.descriptor(id: ChildID("child-\($0)"), startupOrder: $0, command: "sleep 30")
        }
        let supervisor = HostSupervisor(descriptors: descriptors, store: ChildSupervisionStore(), gracefulShutdownTimeout: 7)

        #expect(supervisor.worstCaseShutdownDuration == 21)
    }

    /// A disabled child is never in `startupOrder` (filtered at `init`), so
    /// it must not inflate the worst case either — the same descriptor set
    /// as above, plus one disabled child, must still report the same value.
    @Test
    func worstCaseShutdownDurationExcludesDisabledChildren() {
        let enabled = Self.descriptor(id: "enabled-child", startupOrder: 0, command: "sleep 30")
        let disabled = ChildDescriptor(
            id: "disabled-child",
            displayName: "Disabled",
            transport: .loopbackHTTP,
            endpoint: URL(string: "http://127.0.0.1:9999/disabled")!,
            healthCheck: .http(URL(string: "http://127.0.0.1:9999/disabled")!),
            healthCheckTimeout: 1,
            startupOrder: 1,
            adoptionPolicy: .spawnOnly,
            launch: ChildLaunch(),
            isEnabled: false
        )
        let supervisor = HostSupervisor(descriptors: [enabled, disabled], store: ChildSupervisionStore(), gracefulShutdownTimeout: 7)

        #expect(supervisor.worstCaseShutdownDuration == 7)
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

    private static func waitForPid(at url: URL, timeout: TimeInterval) async throws -> pid_t {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if let contents = try? String(contentsOf: url, encoding: .utf8),
               let pid = pid_t(contents.trimmingCharacters(in: .whitespacesAndNewlines)) {
                return pid
            }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        struct TimedOutWaitingForPidFile: Error {}
        throw TimedOutWaitingForPidFile()
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

