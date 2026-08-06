import Foundation
import os
#if canImport(Darwin)
import Darwin
#endif

/// Starts every enabled child in ascending declared `startupOrder`, restarts
/// a child that exits unexpectedly with exponential backoff (surfacing
/// repeated failure rather than retrying silently), and shuts every child
/// down in the reverse of its startup order — requesting graceful
/// termination before escalating.
///
/// **`awaitExit(of:)` single-ownership, enforced structurally rather than by
/// convention:** the only call to the free function `awaitExit(of:)` for a
/// *live* pid is inside `apply(_:id:)`'s `.spawned` case, which is
/// `private` and reachable only from `startChild` and from `handleExit`'s own
/// restart path — never from an external caller. Each generation of a
/// child's process gets exactly one `Task` that awaits its pid; that `Task`
/// is what schedules the *next* generation's `Task` (from inside
/// `handleExit`, after backoff), so there is never a moment where two
/// `Task`s are both actively waiting on the same pid. `shutdownChild` calls
/// `awaitExit` a second time only in the escalation path, and only after
/// confirming (via `task.value`) that the first wait has fully relinquished
/// ownership — see its own documentation, including why that second call is
/// shielded from cancellation rather than merely bounded by it.
public actor HostSupervisor {
    private struct ChildState {
        let descriptor: ChildDescriptor
        var backoff: RestartBackoff
        var consecutiveUnstableCrashes = 0
        var spawnedChild: SpawnedChild?
        var startedAt: Date?
        var intentionalStop = false
        var supervisionTask: Task<Void, Never>?

        /// The two tasks (section 5.1) draining this generation's stdout
        /// and stderr into `logStore`. Owned the same way `supervisionTask`
        /// is — one pair per live generation — but never `await`ed: see
        /// `cancelLogReaders(_:_:)`.
        ///
        /// Two named fields, not `[Task<Void, Never>]`. Bisection during
        /// this block's development found that adding an
        /// `[Task<Void, Never>]` array field to this struct — even left
        /// completely unpopulated, with no code path ever writing to it —
        /// made a real restart cycle with real stdout output reproducibly
        /// crash with heap corruption (`malloc`: "freed pointer was not the
        /// last allocation") under this toolchain (Swift 6.3.3); reverting
        /// to two plain optionals, with no other change, made the crash
        /// stop reproducing across repeated runs. **The cause is suspected,
        /// not confirmed**: neither this block's reviewer nor its
        /// supervisor could identify an actual overlapping-access bug in
        /// the surrounding code (the `states[id]` read-await-writeback
        /// pattern this struct is stored under predates this block and is
        /// unchanged by it), so this may be a toolchain defect rather than
        /// a defect here — logged as a fact pattern for whoever hits
        /// something like it next, not as a settled root cause. The shipped
        /// two-field shape needs no unproven cause to justify it: it works,
        /// and reads naturally next to `supervisionTask`.
        var stdoutReaderTask: Task<Void, Never>?
        var stderrReaderTask: Task<Void, Never>?
    }

    /// Default for the `logDrainGrace` injection point below — see that
    /// parameter's own documentation for what the grace period is for. 2s
    /// specifically: a pipe that is genuinely still draining finishes in
    /// milliseconds, so this is generous headroom for that, not a value
    /// tuned against any particular child's behaviour. Named and `public`
    /// so `HostRuntime`'s own matching default can reference this exact
    /// value instead of restating the literal — the same "derive, don't
    /// restate" rule `worstCaseShutdownDuration` enforces for shutdown
    /// timing, applied to the one place in this section that didn't yet
    /// follow it. A computed property, not a stored `static let`: a stored
    /// static constant here — tried first — reproducibly triggered this
    /// package's known toolchain-sensitive heap corruption (`malloc`:
    /// "freed pointer was not the last allocation"; see `ChildState
    /// .stdoutReaderTask`'s own documentation for the same signature from
    /// an unrelated cause), even though nothing about a `static let`
    /// touches this actor's instance layout. Bisected the same way that
    /// defect was: reverting to a computed property, with no other change,
    /// made the crash stop reproducing across repeated runs.
    public static var defaultLogDrainGrace: TimeInterval { 2 }

    private let coordinator: ChildStartCoordinator
    private let spawner: ChildSpawner
    private let store: ChildSupervisionStore
    private let logStore: ChildLogStore
    private let repeatedFailureThreshold: Int
    private let gracefulShutdownTimeout: TimeInterval
    private let logDrainGrace: TimeInterval
    private let sleep: @Sendable (TimeInterval) async -> Void

    /// Enabled child ids, ascending by `startupOrder`. `shutdown()` walks
    /// this in reverse.
    private let startupOrder: [ChildID]
    private var states: [ChildID: ChildState] = [:]

    public init(
        descriptors: [ChildDescriptor],
        coordinator: ChildStartCoordinator = ChildStartCoordinator(),
        spawner: ChildSpawner = ChildSpawner(),
        store: ChildSupervisionStore,
        logStore: ChildLogStore = ChildLogStore(),
        backoff: RestartBackoff = RestartBackoff(),
        // Independent of `backoff.maximum` on purpose: the delay sequence is
        // a pacing knob, this is a human-alerting knob, and retuning one
        // must never silently retune the other. See `ChildState
        // .consecutiveUnstableCrashes`.
        repeatedFailureThreshold: Int = 5,
        gracefulShutdownTimeout: TimeInterval = 5,
        // How long a crashed generation's orphaned reader tasks are left
        // running before they are cancelled (task 5.1 remediation). Exists
        // so a sustained crash-restart loop with a grandchild holding a
        // pipe's write end open (EOF never arrives) leaks a bounded amount
        // per crash instead of one reader task and two fds *forever*. A
        // policy value, so it lives here rather than as a literal buried in
        // `handleExit`, and is injectable so tests can drive it down to
        // make the grace itself fast to observe. Defaults to
        // `defaultLogDrainGrace` (see its own documentation for why 2s)
        // rather than a second `2` literal here, so `HostRuntime`'s
        // matching default cannot silently drift out of sync with it.
        logDrainGrace: TimeInterval = HostSupervisor.defaultLogDrainGrace,
        sleep: @escaping @Sendable (TimeInterval) async -> Void = { seconds in
            try? await Task.sleep(nanoseconds: UInt64(max(seconds, 0) * 1_000_000_000))
        }
    ) {
        self.coordinator = coordinator
        self.spawner = spawner
        self.store = store
        self.logStore = logStore
        self.repeatedFailureThreshold = repeatedFailureThreshold
        self.gracefulShutdownTimeout = gracefulShutdownTimeout
        self.logDrainGrace = logDrainGrace
        self.sleep = sleep

        let enabled = descriptors.filter(\.isEnabled).sorted { $0.startupOrder < $1.startupOrder }
        self.startupOrder = enabled.map(\.id)
        for descriptor in enabled {
            states[descriptor.id] = ChildState(descriptor: descriptor, backoff: backoff)
        }
    }

    /// A live feed of every child's current supervision state.
    public func supervisionUpdates() async -> AsyncStream<ChildSupervisionStore.Snapshot> {
        await store.updates()
    }

    /// `id`'s currently spawned pid, if any — `nil` if it was adopted, has
    /// not started yet, or has exited and not yet restarted. Exposed for
    /// tests to distinguish "restarted" (a new pid, or an adoption) from
    /// "never stopped" (the original pid, still alive).
    func currentPID(for id: ChildID) -> pid_t? {
        states[id]?.spawnedChild?.pid
    }

    /// `id`'s last published `ChildSupervisionState`. Exposed for tests to
    /// distinguish a genuine intentional stop from a restart merely
    /// prevented in time — a shutdown that skipped straight to re-reading
    /// `intentionalStop` after the backoff sleep, without ever taking the
    /// early `handleExit` branch, would still not restart, but would leave
    /// the store showing `.restarting`/`.repeatedFailure` rather than
    /// `.stoppedIntentionally`.
    func currentSupervisionState(for id: ChildID) async -> ChildSupervisionState {
        await store.state(for: id)
    }

    /// Starts every enabled child, in ascending `startupOrder`, awaiting each
    /// child's initial start outcome before starting the next.
    public func start() async {
        for id in startupOrder {
            await startChild(id)
        }
    }

    /// Shuts down every enabled child, in the reverse of its declared
    /// startup order, and returns the ids of any it refused to signal — see
    /// `ChildSpawner.killProcessGroup`'s own documentation for the one
    /// refusal reason that exists today. Deliberately not
    /// `@discardableResult`, for the same reason `killProcessGroup` itself
    /// is not: a caller (the app's termination hook) must at least
    /// acknowledge a non-empty result.
    public func shutdown() async -> [ChildID] {
        var refused: [ChildID] = []
        for id in startupOrder.reversed() {
            if let refusedID = await shutdownChild(id) {
                refused.append(refusedID)
            }
        }
        return refused
    }

    /// The worst case `shutdown()` could take if every enabled child's whole
    /// process group ignored `SIGTERM`: each child's own
    /// `gracefulShutdownTimeout` wait, for as many children as this host
    /// supervises — `shutdown()` walks them strictly sequentially (task
    /// 4.5), so these do not overlap and the total is a plain product, not
    /// a race. A caller deriving an app-exit termination budget from this
    /// gets a value that changes automatically when the inventory or the
    /// timeout does, instead of a hand-computed constant that can silently
    /// drift out of sync with either.
    ///
    /// `nonisolated`: both inputs are `let`-bound `Sendable` values fixed at
    /// `init` and never mutated afterward, so reading them needs no actor
    /// hop — a caller computing a budget at app-launch does not need to
    /// `await` into this actor first.
    public nonisolated var worstCaseShutdownDuration: TimeInterval {
        gracefulShutdownTimeout * TimeInterval(startupOrder.count)
    }

    // MARK: - Starting and restarting

    private func startChild(_ id: ChildID) async {
        guard let descriptor = states[id]?.descriptor else { return }
        let outcome = await coordinator.start(descriptor)
        await apply(outcome, id: id)
    }

    /// Applies a start (or restart) outcome for `id`, publishing the
    /// resulting supervision state and — for a freshly spawned child —
    /// starting the one `Task` that will ever call `awaitExit(of:)` for this
    /// pid while it is genuinely live.
    ///
    /// **Mutates `states[id]` directly, field by field, in place — never
    /// takes (or returns) a `ChildState` copy.** An earlier version took
    /// `state: inout ChildState`: the caller read `states[id]` into a local
    /// once, passed it in by reference, and wrote the whole thing back to
    /// `states[id]` after this returned. That local copy lived across this
    /// function's own `await store.publish(...)` below — and this actor is
    /// reentrant at every `await`, so while this call was suspended there,
    /// nothing stopped a *second*, unrelated call into this actor for the
    /// same `id` from running to completion first. **If** that second call
    /// were the very child this call had just spawned, crashing before this
    /// call ever got to publish, its own `handleExit` would re-enter, read
    /// the *stale* stored entry (this call's spawn hadn't been written back
    /// yet), and could run an entire restart cycle — including spawning a
    /// *third* generation — to completion. When this call finally resumed
    /// and wrote its captured copy back, it would silently overwrite that
    /// third generation's freshly-recorded `SpawnedChild` with its own,
    /// second generation's now-dead one. The third generation would keep
    /// running with no reference to it left anywhere in this actor —
    /// `shutdownChild` would find the wrong, already-dead pid and return
    /// early, and the real, live process would survive host shutdown
    /// entirely, re-parented to launchd. That would be requirement 5
    /// (design D6) failing silently, for a process this host itself
    /// spawned.
    ///
    /// **This shape is real by static trace, not by observation.** Both
    /// this block's reviewer and its supervisor independently confirmed the
    /// race is structurally possible against the reverted `inout`-copy
    /// code — but neither twenty solo repetitions of this exact
    /// single-child scenario, nor this suite's own 40-concurrent-children
    /// storm variant, run against that same reverted code, ever produced a
    /// failure (see `HostSupervisorTests
    /// .aCrashRestartStormEndsWithTheSupervisorTrackingTheNewestGenerationNotAStaleOne`'s
    /// own "What this test actually is, stated plainly" for the full
    /// account of what was and wasn't tried). So: closed **by construction,
    /// not because it was reproduced**. Writing straight through
    /// `states[id]?.field = ...` removes the local copy a later write could
    /// go stale against, so there is nothing left for a reentrant call to
    /// race against — whether the kernel's real exit-delivery timing could
    /// ever actually win that race on this platform remains unverified
    /// either way, and this fix does not depend on the answer.
    private func apply(_ outcome: ChildStartOutcome, id: ChildID) async {
        switch outcome {
        case .adopted:
            states[id]?.spawnedChild = nil
            states[id]?.startedAt = nil
            states[id]?.supervisionTask = nil
            states[id]?.stdoutReaderTask = nil
            states[id]?.stderrReaderTask = nil
            // An adopted child's stdout/stderr belong to whoever launched
            // it — there is physically nothing here to read (`SpawnedChild`
            // is never constructed for `.adopted`). Say so once, attributed
            // to this child, rather than let its section of the pane read
            // as "this child is silent".
            await logStore.append(
                "adopted an already-running process; its output belongs to whoever launched it, so this host has nothing to capture",
                source: .host,
                for: id
            )
            await store.publish(.normal, for: id)

        case .spawned(let child):
            states[id]?.spawnedChild = child
            states[id]?.startedAt = Date()
            states[id]?.supervisionTask = Task { [weak self] in
                switch await awaitExit(of: child.pid) {
                case .cancelled:
                    // Cancelled (by `shutdownChild`, escalating past a
                    // graceful-termination timeout) rather than the process
                    // having actually exited — there is nothing to react to,
                    // and `handleExit` must not run, because there was no
                    // real exit for it to evaluate.
                    return
                case .exited, .reapFailed:
                    // `.reapFailed` should be structurally unreachable here
                    // (this is the one live wait for this pid), but if it
                    // ever occurs, the pid is gone from this process's view
                    // either way — evaluating for restart is still correct.
                    await self?.handleExit(id: id)
                }
            }
            let (stdoutReaderTask, stderrReaderTask) = Self.startLogReaders(for: child, id: id, into: logStore)
            states[id]?.stdoutReaderTask = stdoutReaderTask
            states[id]?.stderrReaderTask = stderrReaderTask
            // Every mutation above this line has already landed in
            // `states[id]` itself — nothing below is a captured value this
            // function still owes a writeback for, so the `await` on the
            // next line (which releases this actor to any reentrant call)
            // can never cause anything recorded above to be lost.
            //
            // Do not publish `.normal` while a crash streak is still open:
            // the spec's whole point is that repeated failure is
            // *surfaced*, and a state that flips to `.normal` for the brief
            // window between a crash and its (also failing) restart attempt
            // would blink the alert away on every iteration of an ongoing
            // loop. `.normal` is only published once the streak has
            // genuinely reset — a stable exit resets it in `handleExit`
            // before this is ever reached again — or this is the very first
            // start, where the counter is still at its initial `0`.
            if states[id]?.consecutiveUnstableCrashes == 0 {
                await store.publish(.normal, for: id)
            }

        case .launchNotDecided, .executableNotResolved, .spawnFailed:
            // Not a crash: there was never a process to exit, so this does
            // not feed `backoff` at all. Nothing enabled in this change's
            // scope (only the network gateway, task 4.7) takes this path
            // with real launch candidates declared.
            states[id]?.spawnedChild = nil
            states[id]?.startedAt = nil
            states[id]?.supervisionTask = nil
            states[id]?.stdoutReaderTask = nil
            states[id]?.stderrReaderTask = nil
        }
    }

    /// Starts the two reader tasks that drain `child`'s stdout and stderr
    /// into `logStore`, entirely off `HostSupervisor`'s actor —
    /// `Task.detached`, never a plain `Task {}` (which would inherit this
    /// actor's isolation and so run its blocking reads on it): a slow or
    /// silent child must never stall the actor's other work.
    private static func startLogReaders(for child: SpawnedChild, id: ChildID, into logStore: ChildLogStore) -> (stdout: Task<Void, Never>, stderr: Task<Void, Never>) {
        let stdout = Task.detached {
            await drainOutput(child.standardOutput, source: .standardOutput, childID: id, into: logStore)
        }
        let stderr = Task.detached {
            await drainOutput(child.standardError, source: .standardError, childID: id, into: logStore)
        }
        return (stdout, stderr)
    }

    /// Reads `handle` line by line until EOF, cancellation, or a read error,
    /// appending each line to `logStore` as it arrives.
    ///
    /// `internal`, not `private`: exercised directly by
    /// `HostSupervisorChildOutputTests` against a `Pipe` whose write end the
    /// test itself holds open and never writes to — the fixture that
    /// exposed the cancellation defect `readChunk` fixes — so genuine
    /// cancellation is provable without needing the full spawn/restart
    /// machinery.
    ///
    /// Three approaches were tried, in order, each rejected on measured
    /// evidence rather than preference — the first two documented here so
    /// nobody re-discovers them the hard way, the third caught only by this
    /// block's own review, not by the tests that shipped alongside it:
    ///
    /// 1. `FileHandle.bytes.lines` reproducibly stalled partway through a
    ///    large (>64 KB) pipe write under this actor/`Task.detached` shape —
    ///    reading stopped advancing well before EOF and only resumed once
    ///    the child had already been force-terminated elsewhere, which is
    ///    the deadlock this task exists to close, not reproduce.
    /// 2. A plain blocking loop over `FileHandle.read(upToCount:)` fixed
    ///    that, but starves Swift's cooperative thread pool once several
    ///    readers are blocked in it concurrently: confirmed by running this
    ///    package's full test suite (not just this file's own tests in
    ///    isolation) with that version — unrelated tests elsewhere in the
    ///    suite (signal handling, HTTP-check timeouts) started stalling for
    ///    ~30s apiece, because every blocked reader thread is a thread the
    ///    rest of the process's async work cannot use.
    /// 3. `DispatchIO`, two different ways, both wrong for the same
    ///    underlying reason. A one-shot `DispatchIO.read(fromFileDescriptor:
    ///    ...)` convenience call per chunk fixed both problems above, but
    ///    its `withCheckedContinuation` had no cancellation handler —
    ///    cancelling the surrounding `Task` was silently inert while a read
    ///    was outstanding, and `defer { handle.closeFile() }` never ran,
    ///    because the function body never returned. Rebuilding it around a
    ///    persistent `DispatchIO` **channel** with `withTaskCancellationHandler`
    ///    calling `channel.close(flags: .stop)` fixed *that* — cancellation
    ///    against a pipe that never writes and never closes genuinely works,
    ///    proven directly — but surfaced a second, worse defect: requesting
    ///    `channel.read(length: 65_536, ...)` only resolves once `length`
    ///    bytes have arrived *or* the channel reaches true EOF. A live child
    ///    that writes one short line and then goes quiet (`ndmon` between
    ///    log lines; the `outputSurvivesARestart` fixture's second
    ///    generation, which writes 13 bytes and then `sleep 30`s) satisfies
    ///    neither, so the read simply never resolved — not stalled, not
    ///    slow, genuinely never — until something *else* (the next crash,
    ///    or `shutdown()`'s own group `SIGKILL`) closed the fd out from
    ///    under it. `channel.setLimit(lowWater: 1)` does not help: it
    ///    changes how eagerly *intermediate* `done: false` callbacks fire,
    ///    but this code was only ever resuming its continuation on
    ///    `done: true`, so intermediate deliveries were received and
    ///    silently accumulated forever, never handed back to the caller.
    ///
    /// This version drops `DispatchIO` entirely and uses a bare
    /// `DispatchSourceRead`, the same family `ProcessExit.swift`'s
    /// `awaitExit(of:)` already bridges into async/await for process-exit
    /// events (`ReadWaitBox` below mirrors that file's `ExitWaitBox`
    /// structurally). A read source fires its event handler whenever the fd
    /// has *any* data ready to read — not "length bytes or EOF" — so a
    /// single short write followed by silence is delivered immediately, and
    /// a genuine `read(2)` syscall (non-blocking here, since the source only
    /// fires when data is actually available) drains whatever is currently
    /// buffered, up to 64 KiB per call, looping for a burst larger than
    /// that. Cancellation cancels the source and resumes with `nil` exactly
    /// once — see `ReadWaitBox`.
    ///
    /// **Exactly one owner closes this fd: `handle`, via `handle.closeFile()`
    /// in the `defer` below.** Nothing else in this function ever touches
    /// `close(2)` on it — `DispatchSourceRead` only *watches* the fd, the
    /// same way `DispatchSource.makeProcessSource` only watches a pid
    /// without reaping it — so there is no second closer to race or
    /// double-close against.
    ///
    /// Splits on `\n` manually, buffering any partial line across reads in
    /// `pending`; a final line with no trailing newline is flushed once a
    /// read reports EOF, an error, or cancellation, rather than swallowed —
    /// whatever data arrived alongside that final read is kept, not
    /// discarded.
    static func drainOutput(
        _ handle: FileHandle,
        source: ChildLogSource,
        childID: ChildID,
        into logStore: ChildLogStore
    ) async {
        defer { handle.closeFile() }

        var pending = Data()
        let fd = handle.fileDescriptor

        while !Task.isCancelled {
            guard let chunk = await Self.readChunk(fd: fd), !chunk.isEmpty else { break }
            pending.append(chunk)
            while let newlineIndex = pending.firstIndex(of: 0x0A) {
                let lineData = pending[pending.startIndex..<newlineIndex]
                await logStore.append(String(decoding: lineData, as: UTF8.self), source: source, for: childID)
                pending.removeSubrange(pending.startIndex...newlineIndex)
            }
        }

        if !pending.isEmpty {
            await logStore.append(String(decoding: pending, as: UTF8.self), source: source, for: childID)
        }
    }

    /// Awaits `fd` becoming readable and performs one `read(2)` of up to
    /// 64 KiB, cancellable via `ReadWaitBox`. `nil` means "stop reading" —
    /// covers a read error *and* cancellation, neither of which this caller
    /// needs to tell apart, unlike `.reapFailed` vs `.cancelled` in
    /// `ProcessExit.swift`, where the caller genuinely does. Empty `Data`
    /// means clean EOF. Never blocks the calling thread while waiting: the
    /// `DispatchSourceRead` event only fires once the kernel has already
    /// reported the fd readable, so the `read(2)` call inside
    /// `ReadWaitBox.resumeFromReadability()` never itself blocks either.
    private static func readChunk(fd: Int32) async -> Data? {
        let box = ReadWaitBox(fd: fd)
        return await withTaskCancellationHandler(
            operation: {
                await withCheckedContinuation { (continuation: CheckedContinuation<Data?, Never>) in
                    box.start(continuation: continuation)
                }
            },
            onCancel: {
                box.cancel()
            }
        )
    }

    /// Bridges one fd-readable event into async/await — structurally the
    /// same problem `ProcessExit.swift`'s `ExitWaitBox` solves for process
    /// exit, and solved the same way: two independent ways this can settle
    /// (the fd becoming readable, observed on `DispatchSource`'s queue, or
    /// the awaiting `Task` being cancelled, observed on whatever thread
    /// requests cancellation), so exactly one of them resumes the
    /// continuation, never both, regardless of which happens first.
    /// `OSAllocatedUnfairLock` rather than `@unchecked Sendable` /
    /// `nonisolated(unsafe)`, for the same reason `ExitWaitBox` uses it
    /// (ADR/design D14 reserves those for the audio ring buffer only): a
    /// real, checked synchronisation primitive, not a suppressed
    /// diagnostic.
    private final class ReadWaitBox: Sendable {
        private struct State {
            var source: DispatchSourceRead?
            var continuation: CheckedContinuation<Data?, Never>?
            var settled = false
        }

        private let state = OSAllocatedUnfairLock(initialState: State())
        private let fd: Int32

        init(fd: Int32) {
            self.fd = fd
        }

        func start(continuation: CheckedContinuation<Data?, Never>) {
            let source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: .global(qos: .utility))
            source.setEventHandler { [weak self] in
                self?.resumeFromReadability()
            }

            let alreadySettled = state.withLock { s -> Bool in
                if s.settled { return true }
                s.source = source
                s.continuation = continuation
                return false
            }

            guard !alreadySettled else {
                // `cancel()` ran before `start()` could install the source —
                // possible in principle, not observed in practice. Resume
                // without ever having watched anything, rather than leaking
                // `continuation`.
                continuation.resume(returning: nil)
                return
            }
            source.resume()
        }

        private func resumeFromReadability() {
            let continuationToResume = state.withLock { s -> CheckedContinuation<Data?, Never>? in
                guard !s.settled else { return nil }
                s.settled = true
                s.source?.cancel()
                s.source = nil
                let c = s.continuation
                s.continuation = nil
                return c
            }
            guard let continuationToResume else { return }

            var buffer = [UInt8](repeating: 0, count: 65_536)
            let bytesRead = buffer.withUnsafeMutableBytes { raw in
                Darwin.read(fd, raw.baseAddress, raw.count)
            }
            switch bytesRead {
            case ..<0:
                // A read error on an fd the source just reported readable —
                // nothing further to capture from it either way.
                continuationToResume.resume(returning: nil)
            case 0:
                continuationToResume.resume(returning: Data())
            default:
                continuationToResume.resume(returning: Data(buffer[0..<bytesRead]))
            }
        }

        func cancel() {
            let continuationToResume = state.withLock { s -> CheckedContinuation<Data?, Never>? in
                guard !s.settled else { return nil }
                s.settled = true
                s.source?.cancel()
                s.source = nil
                let c = s.continuation
                s.continuation = nil
                return c
            }
            continuationToResume?.resume(returning: nil)
        }
    }

    /// Cancels a generation's reader tasks without waiting for them —
    /// mirrors why `shutdownChild` never `await`s `supervisionTask` on the
    /// escalation path. A grandchild that inherited the write end of either
    /// pipe can keep it open past this child's own exit, so a read may
    /// otherwise never see EOF; cancelling propagates into `readChunk`'s
    /// `withTaskCancellationHandler`, which calls `ReadWaitBox.cancel()` and
    /// lets `drainOutput` actually return — see its own documentation. Safe
    /// to call more than once for the same tasks — `Task.cancel()` is
    /// idempotent.
    private func cancelLogReaders(_ stdout: Task<Void, Never>?, _ stderr: Task<Void, Never>?) {
        stdout?.cancel()
        stderr?.cancel()
    }

    /// Bounds the leak `handleExit`'s own documentation describes: gives an
    /// orphaned generation's reader tasks `logDrainGrace` to finish draining
    /// whatever is genuinely still buffered in their pipes, then cancels
    /// whichever of them is still running.
    ///
    /// A no-op if both are already `nil` (an adopted or never-started
    /// generation has nothing to schedule). Otherwise a `Task.detached`
    /// deliberately *not* retained anywhere and *not* awaited by
    /// `handleExit` — its own lifetime is bounded by `logDrainGrace` itself
    /// (it does one sleep, then returns), which is a different shape from
    /// the readers it may end up cancelling, whose lifetime is unbounded
    /// without this.
    ///
    /// **Deliberately uses `Task.sleep` directly, never `self.sleep`.**
    /// `sleep` exists to let restart-backoff tests replace real waiting
    /// with an instant, recording fake (see
    /// `HostSupervisorTests.repeatedImmediateCrashesRequestAn...`) — sharing
    /// that same injection point here corrupted exactly that test: every
    /// real crash also schedules a drain-grace wait, and `logDrainGrace`'s
    /// default (`2`) happens to equal the backoff's own `initial` delay, so
    /// a fake `sleep` built to record *only* restart delays silently
    /// recorded an extra, unrelated `2` for every crash too, interleaved
    /// among the genuine ones. The two concerns are unrelated (how long
    /// before the next restart attempt vs. how long before an orphaned
    /// reader is cancelled) and must not be able to contaminate one
    /// another's test double just because they happened to share one
    /// constructor parameter. `logDrainGrace`'s *value* stays fully
    /// configurable (tests needing this grace to elapse quickly, like
    /// `HostSupervisorChildOutputTests`'s fd-bound test, pass a small real
    /// number); only the mechanism that waits it out is no longer shared.
    private func scheduleLogDrainGraceCancellation(_ stdout: Task<Void, Never>?, _ stderr: Task<Void, Never>?) {
        guard stdout != nil || stderr != nil else { return }
        let grace = logDrainGrace
        Task.detached {
            try? await Task.sleep(nanoseconds: UInt64(max(grace, 0) * 1_000_000_000))
            stdout?.cancel()
            stderr?.cancel()
        }
    }

    /// Called once, by the one `Task` supervising `id`'s current pid, when
    /// that pid's `awaitExit` resolves with a genuine exit (never on
    /// cancellation — see `apply`'s `.spawned` case).
    ///
    /// Same rule as `apply`, and for the identical reason (see its doc
    /// comment for the failure this closes in full): every mutation below
    /// goes straight through `states[id]?.field = ...` at the point it is
    /// decided, never via a captured `ChildState` local written back after
    /// one of this function's several `await`s. Values this function only
    /// *reads* to compute something (`uptime`, `isStable`,
    /// `consecutiveUnstableCrashes`, `delay`) are fine as locals — they are
    /// derived facts, not a stand-in for the live entry a later statement
    /// would otherwise overwrite it with.
    private func handleExit(id: ChildID) async {
        guard states[id] != nil else { return }
        let uptime = states[id]?.startedAt.map { Date().timeIntervalSince($0) } ?? 0
        states[id]?.spawnedChild = nil
        states[id]?.startedAt = nil
        // Deliberately does **not** cancel the reader tasks *immediately*
        // here. The process exiting and its pipes finishing draining are
        // two independent facts — a pipe retains whatever was already
        // written but not yet read until the reader actually catches up,
        // so cancelling the instant an exit is *detected* would race
        // genuine, still-buffered trailing output and truncate it.
        // Ordinarily that is moot: once every fd that could still write to
        // this pipe is closed, the reader reaches its own natural EOF and
        // returns on its own, no cancellation needed.
        //
        // But "ordinarily" is not "always" — a grandchild that inherited a
        // pipe's write end and re-parents itself outside this child's
        // process group before this generation ends never lets EOF arrive
        // naturally, and unlike `shutdownChild` (which reaches that case
        // with a group `SIGKILL` before it ever cancels), a bare
        // crash/restart has no kill to fall back on. Left with no backstop
        // at all, a sustained crash-restart loop against such a child would
        // leak one live reader task and two open fds *per crash*, for the
        // host's entire lifetime — this generation is about to be orphaned
        // (nothing in `states[id]` will reference these tasks once this
        // function returns), so nothing else could ever reach them to
        // cancel them later either.
        //
        // `scheduleLogDrainGraceCancellation` is that backstop: it gives
        // this generation's readers `logDrainGrace` to finish draining
        // whatever is genuinely still buffered, then cancels whatever is
        // still running. Fire-and-forget and *not* awaited here — this
        // function must not itself block a restart on an unrelated pipe's
        // drain grace. Captured into locals first (rather than read again
        // after clearing them below) simply because reading-then-clearing
        // in one step is clearer than clearing then trying to read `nil`.
        let orphanedStdoutReader = states[id]?.stdoutReaderTask
        let orphanedStderrReader = states[id]?.stderrReaderTask
        scheduleLogDrainGraceCancellation(orphanedStdoutReader, orphanedStderrReader)
        states[id]?.stdoutReaderTask = nil
        states[id]?.stderrReaderTask = nil

        if states[id]?.intentionalStop == true {
            states[id]?.supervisionTask = nil
            await store.publish(.stoppedIntentionally, for: id)
            return
        }

        // Whether to surface `.repeatedFailure` is driven by its own
        // independent counter, not by `backoff` reaching its cap — the two
        // knobs must be retunable without one silently retuning the other.
        // Both nonetheless key off the same "did it stay up" signal, since
        // that is the correct trigger for both "should pacing reset" and
        // "should the alert streak reset".
        guard
            let stabilityThreshold = states[id]?.backoff.stabilityThreshold,
            let previousUnstableCrashes = states[id]?.consecutiveUnstableCrashes
        else { return }
        let isStable = uptime >= stabilityThreshold
        let consecutiveUnstableCrashes = isStable ? 0 : previousUnstableCrashes + 1
        states[id]?.consecutiveUnstableCrashes = consecutiveUnstableCrashes

        // `nextRestartDelay` mutates `backoff.currentDelay` in place, on
        // the stored entry itself (`states[id]?.backoff...`, not a local
        // copy of `backoff`) — the same reasoning as everywhere else in
        // this function applies to it specifically: a local copy written
        // back after an `await` would let a reentrant call's own write
        // silently overwrite this delay sequence, exactly as it would
        // `spawnedChild` above.
        guard let delay = states[id]?.backoff.nextRestartDelay(afterUptime: uptime) else { return }
        let surfacedState: ChildSupervisionState = consecutiveUnstableCrashes >= repeatedFailureThreshold
            ? .repeatedFailure(delay: delay)
            : .restarting(delay: delay)
        await store.publish(surfacedState, for: id)

        await sleep(delay)

        // Re-check rather than reuse anything read before this sleep: a
        // `shutdown()` may have arrived while this restart was waiting out
        // its backoff delay, and must not be undone by restarting anyway.
        // `descriptor` is a `let` on `ChildState` (never mutated after
        // `init`), so reading it fresh here costs nothing and carries
        // nothing stale either way.
        guard
            let intentionalStop = states[id]?.intentionalStop, !intentionalStop,
            let descriptor = states[id]?.descriptor
        else { return }

        // Restart goes back through `ChildStartCoordinator`, never straight
        // to `ChildSpawner.spawn`: reattach-first (design D6) is not a
        // startup-only property. If something else has taken over the
        // endpoint by the time this runs, adopting it is correct, and
        // spawning directly would create the second process adoption exists
        // to prevent.
        let outcome = await coordinator.start(descriptor)
        await apply(outcome, id: id)
    }

    // MARK: - Shutdown

    /// Returns `id` if this refused to signal `id`'s process group — never
    /// for any other reason (a reap failure past that point is recorded via
    /// `store.publish` alone, since it is a distinct failure mode from
    /// refusing to signal in the first place). `nil` covers every other
    /// outcome, including "nothing live here to begin with".
    ///
    /// Like `apply` and `handleExit`, never captures `states[id]` into a
    /// local `ChildState` and writes it back wholesale after an `await` —
    /// see `apply`'s doc comment for the hazard that pattern carries
    /// elsewhere in this file (real by inspection, not by observation).
    /// `child`, `task`, and the two reader-task
    /// handles captured just below **are** held in locals across this
    /// function's several `await`s, and that is fine: they are this one
    /// generation's own resource handles (the pid to signal, the `Task` to
    /// wait on, the pipes to eventually stop draining), fixed for the
    /// entire lifetime of this call, not the live, shared `ChildState`
    /// itself — nothing here ever writes one of them back into `states[id]`
    /// as part of a struct. `stdoutReaderTask`/`stderrReaderTask`
    /// specifically must be read *before* the wait below, not after: by the
    /// time `task.value` resolves, `handleExit` (running inside `task`) has
    /// already cleared `states[id]`'s own copies to `nil`, so reading them
    /// fresh afterward would find nothing left to cancel.
    private func shutdownChild(_ id: ChildID) async -> ChildID? {
        guard states[id] != nil else { return nil }
        states[id]?.intentionalStop = true

        guard
            let child = states[id]?.spawnedChild,
            let task = states[id]?.supervisionTask
        else {
            // Adopted (never ours to signal), never started, or already
            // exited and mid-way through its own restart decision — either
            // way there is nothing live here to terminate.
            return nil
        }
        let stdoutReaderTask = states[id]?.stdoutReaderTask
        let stderrReaderTask = states[id]?.stderrReaderTask

        let requestedGracefulTermination = spawner.killProcessGroup(of: child, signal: SIGTERM)
        guard requestedGracefulTermination else {
            // Refused because the group would be our own — structurally
            // unreachable for a genuinely spawned child (`ChildSpawner.spawn`
            // already guards against producing one), but the result is not
            // `@discardableResult`, so a refusal is recorded rather than
            // silently assumed away.
            await store.publish(.repeatedFailure(delay: 0), for: id)
            return id
        }

        // `withTimeout` alone cannot bound `await task.value`: `task` is a
        // wholly separate, already-running `Task`, and `Task.value` is not
        // itself a cancellation checkpoint, so `withTimeout`'s own
        // `cancelAll()` — which only cancels its *own* wrapper task — never
        // reaches `task`. Wrapping the wait in a second,
        // `withTaskCancellationHandler`-based layer is what lets a lost race
        // propagate all the way through: cancelling *this* wrapper invokes
        // `onCancel` regardless of what it is suspended on, and `onCancel`
        // explicitly cancels `task` — which `awaitExit` (inside `task`'s own
        // body) now honours, per its own documentation. Without this, a
        // child whose whole process group ignores `SIGTERM` deadlocks
        // `shutdown()` forever, taking every earlier-starting child's
        // termination down with it (`shutdown()` is sequential).
        let exitedGracefully = await withTimeout(gracefulShutdownTimeout) {
            await withTaskCancellationHandler(
                operation: { await task.value; return true },
                onCancel: { task.cancel() }
            )
        } ?? false

        if exitedGracefully {
            // `task` has already run `handleExit` to completion by now
            // (`task.value` cannot resolve until the `Task` closure's own
            // `await self?.handleExit(id: id)` returns), which — since
            // `state.intentionalStop` was set above before this child was
            // ever signalled — has already cleared `spawnedChild` and
            // published `.stoppedIntentionally` for `id`. But that only
            // proves the *leader* exited; requirement 5 (design D6) is
            // "kill that group on exit", not "kill the leader on exit", and
            // a descendant that ignores `SIGTERM` (or is simply still
            // working) survives the leader's own prompt exit undisturbed
            // unless something else reaches it too — the defect this
            // remediation closes.
            //
            // `SIGKILL`ing the group here is safe even though the leader's
            // own pid has already been reaped: POSIX guarantees a
            // process-group id is not reused until the group's *last*
            // member exits, so exactly in the case that matters — a
            // descendant still alive — the group is still occupied and this
            // pgid still reaches it. The only other case is a group that is
            // already fully empty, where `kill(2)` returns `ESRCH`, which
            // `killProcessGroup` intentionally leaves unchecked. This is not
            // a pid-reuse hazard requiring a kill-before-reap ordering: the
            // reservation outlives the reap precisely as long as the group
            // itself does.
            let requestedKill = spawner.killProcessGroup(of: child, signal: SIGKILL)
            if !requestedKill {
                await store.publish(.repeatedFailure(delay: 0), for: id)
            }
            // `handleExit` (which `task` already ran to completion, per the
            // comment block above) deliberately leaves reader tasks running
            // rather than cancel them — see its own documentation. This is
            // therefore their first and only cancellation: it runs after
            // the group `SIGKILL` immediately above, by which point nothing
            // in this child's group can still be writing, so this is a
            // bounded backstop for the one thing that kill cannot reach (a
            // grandchild re-parented outside the group before the kill),
            // not a race against real output.
            cancelLogReaders(stdoutReaderTask, stderrReaderTask)
            return requestedKill ? nil : id
        }

        // By the time `withTimeout` returns here, `task` is guaranteed
        // to have already finished: `withTaskGroup` cannot return until
        // every task it added — including the cancellation-propagating
        // wrapper above, and transitively `task` itself — has completed.
        // `task` therefore gave up its wait (via the cancellation just
        // propagated) without reaping; the process may still be alive.
        let requestedKill = spawner.killProcessGroup(of: child, signal: SIGKILL)
        if !requestedKill {
            await store.publish(.repeatedFailure(delay: 0), for: id)
        }
        // The abandoned wait fully relinquished ownership of this pid
        // before `task` returned, so this is a fresh call, not a second
        // concurrent waiter — reaps whatever `SIGKILL` (or the ignored
        // `SIGTERM`, if it raced in first) actually produced.
        //
        // Shielded from *this* task's own cancellation, deliberately: if
        // whatever drives `shutdown()` is itself cancelled while
        // suspended here, this reap is the last chance to avoid leaving
        // a SIGKILLed-but-unreaped zombie for the host's lifetime —
        // there is no fourth attempt. A plain unstructured `Task` does
        // not inherit or receive its parent's cancellation automatically
        // (only *structured* children — task groups, `async let` — do),
        // so wrapping the reap in one makes it immune by construction,
        // not merely by a comment asserting it should never be needed.
        let reapOutcome = await Task { await awaitExit(of: child.pid) }.value
        if case .reapFailed = reapOutcome {
            // Same reason `!requestedKill` above is surfaced rather than
            // absorbed: this reap is the last chance to reconcile this
            // pid, so its failure deserves the same acknowledgement. Not
            // folded into the returned `ChildID?` — that return means
            // specifically "refused to signal", and a reap failure is a
            // distinct failure mode from that.
            await store.publish(.repeatedFailure(delay: 0), for: id)
        }
        // Unlike the graceful branch above, `handleExit` never ran for this
        // generation — `task` was cancelled, not resolved by a genuine
        // exit — so its reader tasks are still genuinely live here and this
        // is the one place that cancels them.
        cancelLogReaders(stdoutReaderTask, stderrReaderTask)
        // Direct field clears, not a captured-then-written-back copy: this
        // generation is exactly `child`/`task` above, fixed for this call's
        // whole lifetime, so clearing precisely the fields this generation
        // owns is safe regardless of anything else that has touched
        // `states[id]` since they were captured — and nothing else *could*
        // have: `intentionalStop`, set at the very top of this function
        // before any `await`, is what `handleExit` checks before it will
        // ever spawn a new generation, so no reentrant restart can be in
        // flight for `id` for as long as this function is running.
        states[id]?.spawnedChild = nil
        states[id]?.supervisionTask = nil
        states[id]?.stdoutReaderTask = nil
        states[id]?.stderrReaderTask = nil
        await store.publish(.stoppedIntentionally, for: id)
        return requestedKill ? nil : id
    }
}
