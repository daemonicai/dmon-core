import Foundation
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
/// *live* pid is inside `apply(_:id:state:)`'s `.spawned` case, which is
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
    }

    private let coordinator: ChildStartCoordinator
    private let spawner: ChildSpawner
    private let store: ChildSupervisionStore
    private let repeatedFailureThreshold: Int
    private let gracefulShutdownTimeout: TimeInterval
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
        backoff: RestartBackoff = RestartBackoff(),
        // Independent of `backoff.maximum` on purpose: the delay sequence is
        // a pacing knob, this is a human-alerting knob, and retuning one
        // must never silently retune the other. See `ChildState
        // .consecutiveUnstableCrashes`.
        repeatedFailureThreshold: Int = 5,
        gracefulShutdownTimeout: TimeInterval = 5,
        sleep: @escaping @Sendable (TimeInterval) async -> Void = { seconds in
            try? await Task.sleep(nanoseconds: UInt64(max(seconds, 0) * 1_000_000_000))
        }
    ) {
        self.coordinator = coordinator
        self.spawner = spawner
        self.store = store
        self.repeatedFailureThreshold = repeatedFailureThreshold
        self.gracefulShutdownTimeout = gracefulShutdownTimeout
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
        guard var state = states[id] else { return }
        let outcome = await coordinator.start(state.descriptor)
        await apply(outcome, id: id, state: &state)
        states[id] = state
    }

    /// Applies a start (or restart) outcome to `state`, publishing the
    /// resulting supervision state and — for a freshly spawned child —
    /// starting the one `Task` that will ever call `awaitExit(of:)` for this
    /// pid while it is genuinely live.
    private func apply(_ outcome: ChildStartOutcome, id: ChildID, state: inout ChildState) async {
        switch outcome {
        case .adopted:
            state.spawnedChild = nil
            state.startedAt = nil
            state.supervisionTask = nil
            await store.publish(.normal, for: id)

        case .spawned(let child):
            state.spawnedChild = child
            state.startedAt = Date()
            state.supervisionTask = Task { [weak self] in
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
            // Do not publish `.normal` while a crash streak is still open:
            // the spec's whole point is that repeated failure is
            // *surfaced*, and a state that flips to `.normal` for the brief
            // window between a crash and its (also failing) restart attempt
            // would blink the alert away on every iteration of an ongoing
            // loop. `.normal` is only published once the streak has
            // genuinely reset — a stable exit resets it in `handleExit`
            // before this is ever reached again — or this is the very first
            // start, where the counter is still at its initial `0`.
            if state.consecutiveUnstableCrashes == 0 {
                await store.publish(.normal, for: id)
            }

        case .launchNotDecided, .executableNotResolved, .spawnFailed:
            // Not a crash: there was never a process to exit, so this does
            // not feed `backoff` at all. Nothing enabled in this change's
            // scope (only the network gateway, task 4.7) takes this path
            // with real launch candidates declared.
            state.spawnedChild = nil
            state.startedAt = nil
            state.supervisionTask = nil
        }
    }

    /// Called once, by the one `Task` supervising `id`'s current pid, when
    /// that pid's `awaitExit` resolves with a genuine exit (never on
    /// cancellation — see `apply`'s `.spawned` case).
    private func handleExit(id: ChildID) async {
        guard var state = states[id] else { return }
        let uptime = state.startedAt.map { Date().timeIntervalSince($0) } ?? 0
        state.spawnedChild = nil
        state.startedAt = nil

        if state.intentionalStop {
            state.supervisionTask = nil
            states[id] = state
            await store.publish(.stoppedIntentionally, for: id)
            return
        }

        // Whether to surface `.repeatedFailure` is driven by its own
        // independent counter, not by `backoff` reaching its cap — the two
        // knobs must be retunable without one silently retuning the other.
        // Both nonetheless key off the same "did it stay up" signal, since
        // that is the correct trigger for both "should pacing reset" and
        // "should the alert streak reset".
        let isStable = uptime >= state.backoff.stabilityThreshold
        state.consecutiveUnstableCrashes = isStable ? 0 : state.consecutiveUnstableCrashes + 1

        let delay = state.backoff.nextRestartDelay(afterUptime: uptime)
        let surfacedState: ChildSupervisionState = state.consecutiveUnstableCrashes >= repeatedFailureThreshold
            ? .repeatedFailure(delay: delay)
            : .restarting(delay: delay)
        states[id] = state
        await store.publish(surfacedState, for: id)

        await sleep(delay)

        // Re-read rather than reuse `state`: a `shutdown()` may have arrived
        // while this restart was waiting out its backoff delay, and must not
        // be undone by restarting anyway.
        guard var afterDelay = states[id], !afterDelay.intentionalStop else { return }

        // Restart goes back through `ChildStartCoordinator`, never straight
        // to `ChildSpawner.spawn`: reattach-first (design D6) is not a
        // startup-only property. If something else has taken over the
        // endpoint by the time this runs, adopting it is correct, and
        // spawning directly would create the second process adoption exists
        // to prevent.
        let outcome = await coordinator.start(afterDelay.descriptor)
        await apply(outcome, id: id, state: &afterDelay)
        states[id] = afterDelay
    }

    // MARK: - Shutdown

    /// Returns `id` if this refused to signal `id`'s process group — never
    /// for any other reason (a reap failure past that point is recorded via
    /// `store.publish` alone, since it is a distinct failure mode from
    /// refusing to signal in the first place). `nil` covers every other
    /// outcome, including "nothing live here to begin with".
    private func shutdownChild(_ id: ChildID) async -> ChildID? {
        guard var state = states[id] else { return nil }
        state.intentionalStop = true
        states[id] = state

        guard let child = state.spawnedChild, let task = state.supervisionTask else {
            // Adopted (never ours to signal), never started, or already
            // exited and mid-way through its own restart decision — either
            // way there is nothing live here to terminate.
            return nil
        }

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
        if var finalState = states[id] {
            finalState.spawnedChild = nil
            finalState.supervisionTask = nil
            states[id] = finalState
        }
        await store.publish(.stoppedIntentionally, for: id)
        return requestedKill ? nil : id
    }
}
