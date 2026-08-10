import Foundation
import Testing
@testable import Supervisor

/// Task 5.1: `HostSupervisor`'s reader tasks (`apply`'s `.spawned` case) —
/// the buffering itself lives in `ChildLogStore` (see `ChildLogStoreTests`),
/// this exercises the real spawn/restart/shutdown path that feeds it, since
/// the spec's own scenarios ("Child output is visible", "Output survives a
/// child restart") are about what a real supervised child's output does
/// across that path, not about the store in isolation.
@Suite
struct HostSupervisorChildOutputTests {
    // MARK: - Attribution

    /// "Child output is visible", attributed by both child and stream: two
    /// children's stdout and stderr must land in their own buffers, never
    /// cross-attributed to the wrong child.
    @Test
    func outputIsAttributedToTheCorrectChildAndStream() async throws {
        let descriptorA = Self.descriptor(id: "child-a", command: "echo out-from-a; echo err-from-a 1>&2")
        let descriptorB = Self.descriptor(id: "child-b", startupOrder: 1, command: "echo out-from-b; echo err-from-b 1>&2")
        let logStore = ChildLogStore()
        let supervisor = HostSupervisor(
            descriptors: [descriptorA, descriptorB],
            store: ChildSupervisionStore(),
            logStore: logStore,
            backoff: RestartBackoff(initial: 30, maximum: 30, stabilityThreshold: 999)
        )

        await supervisor.start()

        let sawAllFour = await waitUntilTrue(timeout: 3) {
            let a = await logStore.buffer(for: "child-a").lines
            let b = await logStore.buffer(for: "child-b").lines
            return a.contains { $0.text == "out-from-a" && $0.source == .standardOutput }
                && a.contains { $0.text == "err-from-a" && $0.source == .standardError }
                && b.contains { $0.text == "out-from-b" && $0.source == .standardOutput }
                && b.contains { $0.text == "err-from-b" && $0.source == .standardError }
        }
        #expect(sawAllFour)

        let bufferA = await logStore.buffer(for: "child-a").lines
        let bufferB = await logStore.buffer(for: "child-b").lines
        #expect(!bufferA.contains { $0.text.contains("-b") }, "child A's buffer must never contain child B's output")
        #expect(!bufferB.contains { $0.text.contains("-a") }, "child B's buffer must never contain child A's output")

        _ = await supervisor.shutdown()
    }

    // MARK: - Retention across a restart

    /// "Output survives a child restart" — the spec's second scenario, and
    /// the one that must be evaluable without the app. The pre-crash
    /// generation's line must still be present after the child has crashed
    /// and restarted, proving `ChildLogStore` is never cleared between
    /// generations of the same `ChildID`.
    @Test
    func outputSurvivesARestart() async throws {
        let counterFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: counterFile) }
        try "0".write(to: counterFile, atomically: true, encoding: .utf8)

        let script = """
        n=$(( $(cat "\(counterFile.path)") + 1 ))
        echo "$n" > "\(counterFile.path)"
        echo "generation-$n"
        if [ "$n" -eq 1 ]; then
          exit 1
        fi
        sleep 30
        """
        let descriptor = Self.descriptor(id: "restart-output-child", command: script)
        let logStore = ChildLogStore()
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            store: ChildSupervisionStore(),
            logStore: logStore,
            backoff: RestartBackoff(initial: 0.05, maximum: 0.05, stabilityThreshold: 999)
        )

        await supervisor.start()

        let sawBothGenerations = await waitUntilTrue(timeout: 5) {
            let lines = await logStore.buffer(for: descriptor.id).lines.map(\.text)
            return lines.contains("generation-1") && lines.contains("generation-2")
        }
        #expect(sawBothGenerations, "output from the pre-crash generation must still be present after the child restarts")

        _ = await supervisor.shutdown()
    }

    // MARK: - Partial final line

    /// A child that writes a final line with no trailing newline and exits
    /// must still surface it, not swallow it waiting for a newline that
    /// never comes.
    @Test
    func aFinalLineWithNoTrailingNewlineIsStillCaptured() async throws {
        let descriptor = Self.descriptor(id: "partial-line-child", command: "printf boom")
        let logStore = ChildLogStore()
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            store: ChildSupervisionStore(),
            logStore: logStore,
            backoff: RestartBackoff(initial: 30, maximum: 30, stabilityThreshold: 999)
        )

        await supervisor.start()

        let sawBoom = await waitUntilTrue(timeout: 3) {
            await logStore.buffer(for: descriptor.id).lines.contains { $0.text == "boom" }
        }
        #expect(sawBoom, "a final line with no trailing newline must still be captured, not swallowed at EOF")

        _ = await supervisor.shutdown()
    }

    // MARK: - Draining prevents a deadlock

    /// The live defect this block closes: nothing read those pipes before,
    /// and a child that writes more than one pipe buffer's worth (~64 KB)
    /// blocks forever in its own `write(2)` once nothing is draining the
    /// other end — the real, enabled child is `ndmon`, a logging .NET host,
    /// so this is not a theoretical concern. `yes | head` produces well
    /// over 64 KB fast, with no slow interpreted shell loop to flake on.
    ///
    /// Asserts both halves: the process must actually exit (rather than
    /// hang blocked in `write(2)`), and every line it wrote must land in the
    /// store — a store that dropped output as a side effect of unblocking
    /// the pipe (e.g. reading and discarding instead of capturing) would
    /// still pass the completion half but fail the content half.
    @Test
    func aChildWritingMoreThanOnePipeBuffersWorthOfOutputRunsToCompletionAndItsOutputLands() async throws {
        let lineCount = 4000
        let payload = "this-is-a-log-line-with-enough-padding-to-add-up-fast"
        // 4000 * ~56 bytes ≈ 224 KB — well over the ~64 KB pipe buffer that
        // would otherwise wedge an undrained `write(2)` forever.
        let script = "yes '\(payload)' | head -n \(lineCount)"
        let descriptor = Self.descriptor(id: "big-output-child", command: script)
        let logStore = ChildLogStore(capacityPerChild: lineCount + 10)
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            store: ChildSupervisionStore(),
            logStore: logStore,
            backoff: RestartBackoff(initial: 30, maximum: 30, stabilityThreshold: 999)
        )

        await supervisor.start()

        let completed = await waitUntilTrue(timeout: 10) {
            await supervisor.currentPID(for: descriptor.id) == nil
        }
        #expect(completed, "a child writing more than one pipe buffer's worth of output must run to completion, not block forever in write(2) with nothing draining its pipe")

        let fullyDrained = await waitUntilTrue(timeout: 5) {
            await logStore.buffer(for: descriptor.id).lines.count == lineCount
        }
        #expect(fullyDrained, "expected every emitted line to land in the store")

        let lines = await logStore.buffer(for: descriptor.id).lines
        #expect(lines.allSatisfy { $0.text == payload })

        _ = await supervisor.shutdown()
    }

    // MARK: - Cancellation when EOF will never arrive

    /// The exact fixture that exposed the blocker in this round's review: a
    /// pipe whose write end the test itself holds open and never writes
    /// to, so nothing short of cancellation could ever end a reader waiting
    /// on it. Calls `HostSupervisor.drainOutput` directly (it is `internal`
    /// for exactly this) rather than going through the full spawn/restart
    /// path, so this is a test of the read primitive itself, not of
    /// anything `ChildSpawner`- or `HostSupervisor`-specific.
    ///
    /// Falsified directly (not merely asserted): stripping
    /// `readChunk`'s `withTaskCancellationHandler` down to a bare
    /// `withCheckedContinuation` (the version this round's review found)
    /// made `readerTask.value` never resolve — confirmed two ways, not one:
    /// first via `waitUntilTrue`/`withTimeout`, which itself hung rather
    /// than reporting a clean failure, because `withTimeout`'s
    /// `withTaskGroup` cannot return until *every* child task it started
    /// has completed, including a permanently-stuck one, even after
    /// `cancelAll()`; only the race below, which starts the reader-wait and
    /// the timeout as two independent unstructured tasks with no
    /// structured obligation to await the loser, actually reported
    /// `finishedInTime == false` instead of hanging.
    @Test
    func cancellingAReaderTaskEndsItEvenWhenEOFWillNeverArrive() async throws {
        let pipe = Pipe()
        // Deliberately never closed and never written to for the lifetime
        // of this test — this *is* "a live process holding the pipe's
        // write end open, writing nothing"; the test process itself plays
        // that role instead of spawning one.
        let logStore = ChildLogStore()
        let readerTask = Task.detached {
            await HostSupervisor.drainOutput(
                pipe.fileHandleForReading,
                source: .standardOutput,
                childID: "never-eof-child",
                into: logStore
            )
        }

        // Give the reader a moment to actually reach its first read before
        // cancelling, so this exercises "abort an outstanding read" rather
        // than "cancelled before it ever started" — both matter, but the
        // former is the shape `readChunk`'s `withTaskCancellationHandler`
        // exists for.
        try await Task.sleep(nanoseconds: 100_000_000)

        readerTask.cancel()

        // Deliberately *not* `withTimeout` here: `Task.value` is not itself
        // a cancellation checkpoint, so if `readerTask` never actually
        // finishes, `withTimeout`'s own `withTaskGroup` cannot return
        // either — it does not abandon a still-running child task after
        // picking a winner via `group.next()`, it waits for every task the
        // group started before the call as a whole returns. That is
        // exactly correct for bounding a *cooperating* wait (as
        // `shutdownChild` relies on, where the awaited task's own body is
        // already known to honour cancellation) and exactly wrong for a
        // test whose entire point is verifying that cooperation — a
        // genuinely broken `drainOutput` would hang this test's own
        // harness rather than fail it. `RaceBox` below has no such
        // obligation: it resumes on whichever of two fully independent
        // `Task.detached` closures finishes first and leaves the loser
        // running unobserved, so a stuck reader can only ever make this
        // test's *background* leak a task, never hang it.
        let finishedInTime = await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
            let box = RaceBox()
            Task.detached {
                await readerTask.value
                await box.resolve(true, continuation)
            }
            Task.detached {
                try? await Task.sleep(nanoseconds: 2_000_000_000)
                await box.resolve(false, continuation)
            }
        }

        #expect(finishedInTime, "a reader task must finish within a bound once cancelled, even when its pipe's write end is deliberately never closed")

        try? pipe.fileHandleForWriting.close()
    }

    // MARK: - No unbounded fd growth across a sustained crash-restart loop

    /// Architect fold-in, section 5 remediation round: a crashed
    /// generation's reader tasks are deliberately *not* cancelled the
    /// instant an exit is detected (`handleExit`'s own documentation), so a
    /// generation whose grandchild holds a pipe's write end open forever
    /// would, with no backstop, leak one live reader task and two open fds
    /// per crash for the host's entire lifetime. `logDrainGrace` bounds
    /// that. Proven here across five such generations in a row.
    ///
    /// `/dev/fd` entry count is this *process's* real, live fd table, not a
    /// synthetic proxy — deterministic in the sense that mirrors what an
    /// actual leak would do (grow linearly with the number of crashes), but
    /// not perfectly isolated from whatever else this binary's other
    /// concurrently-running tests have open at the same instant, since
    /// `swift test` schedules suites in parallel by default. The bound
    /// below (`initial + 4`) is chosen deliberately far below the failure
    /// signal an unfixed drain grace would produce (`initial + 10`, two fds
    /// per generation × five generations) — small enough that it cannot
    /// paper over the defect this test exists to catch, generous enough to
    /// absorb a handful of stray fds from unrelated concurrent test
    /// activity. Falsified directly: dropping the
    /// `scheduleLogDrainGraceCancellation` call from `handleExit` (so
    /// orphaned readers are never cancelled at all) made this test fail
    /// with a final count around `initial + 10`, not merely close to the
    /// bound.
    @Test
    func fileDescriptorsStayBoundedAcrossRepeatedCrashRestartGenerationsWithAnOrphanedPipeHolder() async throws {
        let counterFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: counterFile) }
        try "0".write(to: counterFile, atomically: true, encoding: .utf8)

        let generationCount = 5
        // Each generation backgrounds a `sleep 30` before crashing — the
        // backgrounded process inherits (and keeps open) this generation's
        // stdout/stderr pipes, so this host's own read ends never see a
        // natural EOF for any of them. Only the drain-grace-then-cancel
        // backstop ever closes them.
        let script = """
        n=$(( $(cat "\(counterFile.path)") + 1 ))
        echo "$n" > "\(counterFile.path)"
        sleep 30 &
        exit 1
        """
        let descriptor = Self.descriptor(id: "fd-leak-child", command: script)
        // `grace` must be comfortably shorter than `backoff`, not the other
        // way around: each generation's drain-grace-then-cancel must have
        // fully finished — closing that generation's two fds — before the
        // *next* crash arrives, or generations pile up faster than they are
        // cleaned and this test would fail for a reason unrelated to the
        // defect it targets. An earlier version of this test used a
        // backoff *faster* than its grace and failed for exactly that
        // reason, independent of whether the drain-grace fix itself works.
        let grace: TimeInterval = 0.05
        let backoff: TimeInterval = 0.3
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            store: ChildSupervisionStore(),
            logStore: ChildLogStore(),
            backoff: RestartBackoff(initial: backoff, maximum: backoff, stabilityThreshold: 999),
            logDrainGrace: grace
        )

        let initialFDCount = Self.openFileDescriptorCount()
        #expect(initialFDCount >= 0, "expected to be able to read /dev/fd")

        await supervisor.start()

        for expectedGeneration in 1...generationCount {
            // `>=`, not `==`: robust against the counter having already
            // moved past `expectedGeneration` by the time this polls, which
            // matters here since nothing about this loop's own pacing
            // controls exactly when each poll lands relative to a crash.
            let reachedGeneration = await waitUntilTrue(timeout: 3) {
                let n = (try? String(contentsOf: counterFile, encoding: .utf8))
                    .flatMap { Int($0.trimmingCharacters(in: .whitespacesAndNewlines)) }
                return (n ?? 0) >= expectedGeneration
            }
            #expect(reachedGeneration, "expected to reach generation \(expectedGeneration)")
            // Let this generation's drain grace elapse (and its readers get
            // cancelled) before the next crash arrives, so each generation
            // is fully settled before the fd count is allowed to reflect it.
            try await Task.sleep(nanoseconds: UInt64((grace + 0.2) * 1_000_000_000))
        }

        _ = await supervisor.shutdown()

        let finalFDCount = Self.openFileDescriptorCount()
        #expect(
            finalFDCount <= initialFDCount + 4,
            "expected fd count to stay bounded across \(generationCount) crash-restart generations (initial \(initialFDCount), final \(finalFDCount)); an unfixed drain grace leaks 2 fds per generation (\(generationCount * 2) here)"
        )
    }

    private static func openFileDescriptorCount() -> Int {
        (try? FileManager.default.contentsOfDirectory(atPath: "/dev/fd"))?.count ?? -1
    }

    // MARK: - Adoption

    /// Architect call 2: an adopted child has no `SpawnedChild` at all —
    /// there is physically nothing to read — so the pane must say so once,
    /// attributed to that child, rather than render an empty section
    /// indistinguishable from "this child is silent".
    @Test
    func anAdoptedChildGetsASingleHostAttributedLine() async throws {
        let coordinator = ChildStartCoordinator(healthChecker: HealthChecker(httpProbe: { _ in true }))
        let descriptor = Self.descriptor(id: "adopted-output-child", policy: .adoptOrSpawn, command: "sleep 30")
        let logStore = ChildLogStore()
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            coordinator: coordinator,
            store: ChildSupervisionStore(),
            logStore: logStore
        )

        await supervisor.start()
        #expect(
            await supervisor.currentPID(for: descriptor.id) == nil,
            "an adoptOrSpawn descriptor whose endpoint already answers should adopt, not spawn"
        )

        let lines = await logStore.buffer(for: descriptor.id).lines
        #expect(lines.count == 1, "adoption should produce exactly one host-attributed line")
        #expect(lines.first?.source == .host)
        #expect(lines.first?.childID == descriptor.id)

        _ = await supervisor.shutdown()
    }

    // MARK: - Launch-failure attribution (task 8.3)

    /// Task 8.3's whole point: today, none of `.launchNotDecided`,
    /// `.executableNotResolved`, `.spawnFailed` wrote anything to the log
    /// pane, so a person watching a failed turn had no way to attribute it
    /// to "this child never even started, and here is why" — the pane just
    /// read "No output yet". `ChildStartOutcome.launchNotDecided`'s own doc
    /// comment distinguishes this from a misconfiguration; the line must say
    /// so, not just report the case name.
    @Test
    func aChildWithNoDecidedLaunchPathGetsAHostAttributedExplanationLine() async throws {
        let descriptor = Self.descriptor(id: "not-decided-child", launch: ChildLaunch())
        let logStore = ChildLogStore()
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            store: ChildSupervisionStore(),
            logStore: logStore
        )

        await supervisor.start()

        let lines = await logStore.buffer(for: descriptor.id).lines
        #expect(lines.count == 1, "an undecided launch path should produce exactly one host-attributed line")
        #expect(lines.first?.source == .host)
        #expect(lines.first?.childID == descriptor.id)
        #expect(lines.first?.text.contains("not decided") == true)
        #expect(lines.first?.text.contains("fault") == true, "must read as a configuration state, not a fault — see ChildStartOutcome.launchNotDecided")

        _ = await supervisor.shutdown()
    }

    /// The misconfiguration case `ChildStartOutcome.executableNotResolved`'s
    /// own doc comment exists to make unmissable: candidates *were*
    /// declared, but none resolved. The host line must name what was tried,
    /// not just say "failed" — that is the difference between an actionable
    /// message and a dead end.
    @Test
    func aChildWhoseDeclaredCandidatesNoneResolveGetsAHostAttributedExplanationNamingThem() async throws {
        let missingPath = "/definitely/does/not/exist/\(UUID().uuidString)"
        let descriptor = Self.descriptor(id: "unresolved-child", launch: ChildLaunch(candidates: [.absolutePath(missingPath)]))
        let logStore = ChildLogStore()
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            store: ChildSupervisionStore(),
            logStore: logStore
        )

        await supervisor.start()

        let lines = await logStore.buffer(for: descriptor.id).lines
        #expect(lines.count == 1, "an unresolved set of candidates should produce exactly one host-attributed line")
        #expect(lines.first?.source == .host)
        #expect(lines.first?.childID == descriptor.id)
        #expect(lines.first?.text.contains(missingPath) == true, "the host line should name the candidate that was tried")

        _ = await supervisor.shutdown()
    }

    /// A candidate resolved, but `posix_spawn` itself failed — the host
    /// line must carry `SpawnError`'s own detail (here, the real errno
    /// `posix_spawn` returns for a directory: `EACCES`), not merely say
    /// "spawn failed". A directory is used as the "executable" deliberately:
    /// it passes `ExecutableResolver`'s own `isExecutableFile` check (a
    /// directory has the search/execute bit) but `posix_spawn` refuses it —
    /// a real, reachable-through-the-real-coordinator failure, not a mocked
    /// one.
    @Test
    func aChildWhosePosixSpawnFailsGetsAHostAttributedExplanationCarryingTheUnderlyingError() async throws {
        let unexecutableDirectory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: unexecutableDirectory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: unexecutableDirectory) }
        #expect(
            FileManager.default.isExecutableFile(atPath: unexecutableDirectory.path),
            "a directory must pass the resolver's own executable check for this fixture to reach posix_spawn at all"
        )

        let descriptor = Self.descriptor(id: "spawn-fail-child", launch: ChildLaunch(candidates: [.absolutePath(unexecutableDirectory.path)]))
        let logStore = ChildLogStore()
        let supervisor = HostSupervisor(
            descriptors: [descriptor],
            store: ChildSupervisionStore(),
            logStore: logStore
        )

        await supervisor.start()

        let lines = await logStore.buffer(for: descriptor.id).lines
        #expect(lines.count == 1, "a failed posix_spawn should produce exactly one host-attributed line")
        #expect(lines.first?.source == .host)
        #expect(lines.first?.childID == descriptor.id)
        #expect(lines.first?.text.contains("posix_spawn failed") == true)
        #expect(lines.first?.text.contains("13") == true, "should carry SpawnError's own errno detail (EACCES, 13)")

        _ = await supervisor.shutdown()
    }

    // MARK: - Helpers

    private static func descriptor(
        id: ChildID,
        startupOrder: Int = 0,
        policy: AdoptionPolicy = .spawnOnly,
        endpoint: URL = URL(string: "http://127.0.0.1:9999/unused")!,
        command: String
    ) -> ChildDescriptor {
        descriptor(
            id: id,
            startupOrder: startupOrder,
            policy: policy,
            endpoint: endpoint,
            launch: ChildLaunch(candidates: [.absolutePath("/bin/sh")], arguments: ["-c", command])
        )
    }

    private static func descriptor(
        id: ChildID,
        startupOrder: Int = 0,
        policy: AdoptionPolicy = .spawnOnly,
        endpoint: URL = URL(string: "http://127.0.0.1:9999/unused")!,
        launch: ChildLaunch
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
            launch: launch,
            isEnabled: true
        )
    }
}

/// Resumes a single `CheckedContinuation` with whichever of two competing
/// `Task.detached` closures calls `resolve` first, ignoring the second
/// call — used only by `cancellingAReaderTaskEndsItEvenWhenEOFWillNeverArrive`,
/// where a stuck loser must never be waited on (see that test's own
/// documentation for why `withTimeout` cannot be used here instead).
private actor RaceBox {
    private var settled = false

    func resolve(_ value: Bool, _ continuation: CheckedContinuation<Bool, Never>) {
        guard !settled else { return }
        settled = true
        continuation.resume(returning: value)
    }
}
