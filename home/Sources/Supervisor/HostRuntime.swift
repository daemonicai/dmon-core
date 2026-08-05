import Foundation

/// One supervised child's merged, UI-facing state — `ChildHealthStore` and
/// `ChildSupervisionStore` publish independently (health-check results and
/// crash-restart history respectively), and this is the two joined by
/// `ChildID` plus the descriptor's static `displayName`, so a UI needs no
/// merging logic of its own.
public struct ChildStatus: Hashable, Sendable {
    public let id: ChildID
    public let displayName: String
    public let health: ChildHealth
    public let supervision: ChildSupervisionState
}

/// The app's composition root for supervision (design D4: the app target is
/// a shell, so this — not the app — is what `swift test` exercises).
///
/// Owns a `HostSupervisor` over the enabled children in `children`, a
/// `HealthMonitor` over those same enabled children (see `healthEntities`
/// for why `monitors` is accepted but not, today, fed to it), and both
/// stores those two write into. Also merges the stores' independent
/// snapshots into one `[ChildStatus]` feed, so the app target's UI mirror
/// only has to consume a stream and store a value — never decide anything.
public actor HostRuntime {
    public let healthStore: ChildHealthStore
    public let supervisionStore: ChildSupervisionStore

    /// Every supervised child's captured stdout/stderr (section 5.1),
    /// exposed the same way `healthStore`/`supervisionStore` are — a live
    /// `AsyncStream` an observer subscribes to directly, rather than
    /// something merged into `statusUpdates()`. Unlike health and
    /// supervision, which the app renders as one joined `[ChildStatus]` row
    /// per child, output is its own pane and the app can attribute each
    /// line to its child using `ChildLogLine.childID` and the display names
    /// already available from `statusUpdates()` — no merging logic
    /// belongs on either side for that.
    public let logStore: ChildLogStore

    private let supervisor: HostSupervisor
    private let healthMonitor: HealthMonitor
    private let healthEntities: [any HealthCheckable]

    /// The enabled children's ids, in the order `HostSupervisor` tracks
    /// them — `statusUpdates()` reports exactly this set. Monitors are
    /// deliberately excluded: they have no `SpawnedChild`/supervision
    /// concept at all (`MonitorDescriptor` carries no `launch` or
    /// `adoptionPolicy`), so folding them into `ChildStatus` would default
    /// their supervision state to `.normal` in a way that asserts an
    /// observation ("this was never spawned") never actually made about
    /// them.
    private let supervisedIDs: [ChildID]
    private let displayNames: [ChildID: String]

    private var healthMonitorTask: Task<Void, Never>?
    private var healthForwardingTask: Task<Void, Never>?
    private var supervisionForwardingTask: Task<Void, Never>?

    private var latestHealth: ChildHealthStore.Snapshot = [:]
    private var latestSupervision: ChildSupervisionStore.Snapshot = [:]
    private var statusSubscribers: [Int: AsyncStream<[ChildStatus]>.Continuation] = [:]
    private var nextStatusSubscriberToken = 0

    public init(
        children: [ChildDescriptor] = ChildInventory.children,
        monitors: [MonitorDescriptor] = ChildInventory.monitors,
        coordinator: ChildStartCoordinator = ChildStartCoordinator(),
        spawner: ChildSpawner = ChildSpawner(),
        healthChecker: HealthChecker = HealthChecker(),
        healthCheckInterval: TimeInterval = 30,
        backoff: RestartBackoff = RestartBackoff(),
        repeatedFailureThreshold: Int = 5,
        gracefulShutdownTimeout: TimeInterval = 5,
        logDrainGrace: TimeInterval = 2,
        healthStore: ChildHealthStore = ChildHealthStore(),
        supervisionStore: ChildSupervisionStore = ChildSupervisionStore(),
        logStore: ChildLogStore = ChildLogStore(),
        sleep: @escaping @Sendable (TimeInterval) async -> Void = { seconds in
            try? await Task.sleep(nanoseconds: UInt64(max(seconds, 0) * 1_000_000_000))
        }
    ) {
        self.healthStore = healthStore
        self.supervisionStore = supervisionStore
        self.logStore = logStore
        self.supervisor = HostSupervisor(
            descriptors: children,
            coordinator: coordinator,
            spawner: spawner,
            store: supervisionStore,
            logStore: logStore,
            backoff: backoff,
            repeatedFailureThreshold: repeatedFailureThreshold,
            gracefulShutdownTimeout: gracefulShutdownTimeout,
            logDrainGrace: logDrainGrace,
            sleep: sleep
        )
        self.healthMonitor = HealthMonitor(checker: healthChecker, store: healthStore, interval: healthCheckInterval)

        let enabledChildren = children.filter(\.isEnabled)
        // `monitors` is deliberately **not** folded in here (Product Owner
        // decision, 2026-08-04): `statusUpdates()` already excludes
        // monitors from its merged feed (see `supervisedIDs` below), so
        // checking them today bought nothing but standing cost — a
        // DNS+TLS request to `egress`'s external endpoint every
        // `healthCheckInterval`, two more against services this change
        // never starts, and a `.process`-kind check (`tailscale`)
        // `HealthChecker` cannot execute at all, so it could only ever
        // report `.unknown`. The `monitors` parameter itself stays (so
        // `ChildInventory.monitors` remains representable and pluggable,
        // per requirement 6), it is simply not wired to this loop yet.
        // Re-enable by adding `monitors.map { $0 as any HealthCheckable }`
        // back below, once something actually consumes a monitor's
        // `ChildHealthStore` entry — a status feed for monitors, which does
        // not exist yet.
        self.healthEntities = enabledChildren.map { $0 as any HealthCheckable }
        self.supervisedIDs = enabledChildren.map(\.id)
        self.displayNames = Dictionary(uniqueKeysWithValues: enabledChildren.map { ($0.id, $0.displayName) })
    }

    /// Starts every enabled child (reattach-first, via `HostSupervisor
    /// .start()`) and starts the recurring health-check loop as a
    /// cancellable `Task` this actor owns, plus the two `Task`s that forward
    /// each store's live updates into the merged `statusUpdates()` feed.
    public func start() async {
        await supervisor.start()

        let entities = healthEntities
        let monitor = healthMonitor
        healthMonitorTask = Task { await monitor.run(entities) }

        let health = healthStore
        healthForwardingTask = Task { [weak self] in
            for await snapshot in await health.updates() {
                await self?.applyHealth(snapshot)
            }
        }

        let supervision = supervisionStore
        supervisionForwardingTask = Task { [weak self] in
            for await snapshot in await supervision.updates() {
                await self?.applySupervision(snapshot)
            }
        }
    }

    /// Cancels the health-check loop, then gracefully shuts down every
    /// enabled child in reverse startup order — killing each one's whole
    /// process group (not just its leader) once its own graceful wait
    /// resolves, whichever way it resolves (see `HostSupervisor
    /// .shutdown()`), so no spawned descendant survives this call.
    ///
    /// Returns the ids `shutdown()` refused to signal — not
    /// `@discardableResult`, for the same reason `HostSupervisor.shutdown()`
    /// itself is not: a caller (the app's termination hook) must at least
    /// acknowledge a non-empty result.
    public func shutdownForTermination() async -> [ChildID] {
        healthMonitorTask?.cancel()
        healthMonitorTask = nil

        let refused = await supervisor.shutdown()

        healthForwardingTask?.cancel()
        healthForwardingTask = nil
        supervisionForwardingTask?.cancel()
        supervisionForwardingTask = nil

        return refused
    }

    /// See `HostSupervisor.worstCaseShutdownDuration` — the one fact an
    /// app-exit termination budget must be derived from rather than
    /// restate, so enabling a new child or retuning `gracefulShutdownTimeout`
    /// cannot make a hand-computed budget silently wrong. `nonisolated` for
    /// the same reason the value it forwards is: both are `let`-bound,
    /// fixed at `init`, so a caller does not need to `await` into this actor
    /// just to size a budget before anything has even started.
    public nonisolated var worstCaseShutdownDuration: TimeInterval {
        supervisor.worstCaseShutdownDuration
    }

    /// A live feed of every enabled child's merged status: the current
    /// snapshot immediately, then one per subsequent health or supervision
    /// change. Mirrors `ChildHealthStore.updates()`'s subscriber pattern.
    public func statusUpdates() -> AsyncStream<[ChildStatus]> {
        let token = nextStatusSubscriberToken
        nextStatusSubscriberToken += 1
        let initial = currentStatus()
        return AsyncStream(bufferingPolicy: .unbounded) { continuation in
            statusSubscribers[token] = continuation
            continuation.yield(initial)
            continuation.onTermination = { [weak self] _ in
                Task { await self?.removeStatusSubscriber(token) }
            }
        }
    }

    private func applyHealth(_ snapshot: ChildHealthStore.Snapshot) {
        latestHealth = snapshot
        publishStatus()
    }

    private func applySupervision(_ snapshot: ChildSupervisionStore.Snapshot) {
        latestSupervision = snapshot
        publishStatus()
    }

    private func publishStatus() {
        let statuses = currentStatus()
        for subscriber in statusSubscribers.values {
            subscriber.yield(statuses)
        }
    }

    private func currentStatus() -> [ChildStatus] {
        supervisedIDs.map { id in
            ChildStatus(
                id: id,
                displayName: displayNames[id] ?? id.rawValue,
                health: latestHealth[id] ?? .unknown,
                supervision: latestSupervision[id] ?? .normal
            )
        }
    }

    private func removeStatusSubscriber(_ token: Int) {
        statusSubscribers.removeValue(forKey: token)
    }
}
