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
/// `HealthMonitor` over those same enabled children plus every entry in
/// `monitors`, and both stores those two write into. Also merges the
/// stores' independent snapshots into one `[ChildStatus]` feed, so the app
/// target's UI mirror only has to consume a stream and store a value —
/// never decide anything.
public actor HostRuntime {
    public let healthStore: ChildHealthStore
    public let supervisionStore: ChildSupervisionStore

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
        healthStore: ChildHealthStore = ChildHealthStore(),
        supervisionStore: ChildSupervisionStore = ChildSupervisionStore(),
        sleep: @escaping @Sendable (TimeInterval) async -> Void = { seconds in
            try? await Task.sleep(nanoseconds: UInt64(max(seconds, 0) * 1_000_000_000))
        }
    ) {
        self.healthStore = healthStore
        self.supervisionStore = supervisionStore
        self.supervisor = HostSupervisor(
            descriptors: children,
            coordinator: coordinator,
            spawner: spawner,
            store: supervisionStore,
            backoff: backoff,
            repeatedFailureThreshold: repeatedFailureThreshold,
            gracefulShutdownTimeout: gracefulShutdownTimeout,
            sleep: sleep
        )
        self.healthMonitor = HealthMonitor(checker: healthChecker, store: healthStore, interval: healthCheckInterval)

        let enabledChildren = children.filter(\.isEnabled)
        self.healthEntities = enabledChildren.map { $0 as any HealthCheckable } + monitors.map { $0 as any HealthCheckable }
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

    /// Cancels the health-check loop, gracefully shuts down every enabled
    /// child in reverse startup order, then sweeps: kills the process group
    /// of any child still left with a live `SpawnedChild` — a backstop for
    /// `shutdown()` having been cut short (see `HostSupervisor
    /// .terminateSpawnedProcessGroups()`), a no-op after a clean shutdown.
    ///
    /// Returns the ids the sweep refused to signal, exactly as
    /// `HostSupervisor.terminateSpawnedProcessGroups()` does — not
    /// `@discardableResult`, for the same reason: a caller (the app's
    /// termination hook) must at least acknowledge a non-empty result.
    public func shutdownForTermination() async -> [ChildID] {
        healthMonitorTask?.cancel()
        healthMonitorTask = nil

        await supervisor.shutdown()
        let refused = await supervisor.terminateSpawnedProcessGroups()

        healthForwardingTask?.cancel()
        healthForwardingTask = nil
        supervisionForwardingTask?.cancel()
        supervisionForwardingTask = nil

        return refused
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
