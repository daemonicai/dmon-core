import Foundation

/// Checks a set of `HealthCheckable` entities and publishes each result into a
/// `ChildHealthStore`.
///
/// Every entity is checked concurrently, each bounded independently by its
/// own `healthCheckTimeout` (via `HealthChecker.check`): a hung check delays
/// only its own publication, never another entity's check or publish.
public struct HealthMonitor: Sendable {
    private let checker: HealthChecker
    private let store: ChildHealthStore
    private let interval: TimeInterval

    /// - Parameter interval: How often `run` repeats `checkOnce`. Injectable
    ///   so tests can drive `checkOnce` directly instead of waiting on
    ///   wall-clock time.
    public init(checker: HealthChecker = HealthChecker(), store: ChildHealthStore, interval: TimeInterval = 30) {
        self.checker = checker
        self.store = store
        self.interval = interval
    }

    /// Checks every entity in `entities` once, concurrently, publishing each
    /// result to the store as it completes rather than waiting for the
    /// slowest to finish before publishing any of them.
    public func checkOnce(_ entities: [any HealthCheckable]) async {
        let checker = self.checker
        let store = self.store
        await withTaskGroup(of: Void.self) { group in
            for entity in entities {
                group.addTask {
                    let health = await checker.check(entity.healthCheck, timeout: entity.healthCheckTimeout)
                    await store.publish(health, for: entity.id)
                }
            }
        }
    }

    /// Repeats `checkOnce` every `interval` seconds until the surrounding
    /// task is cancelled.
    public func run(_ entities: [any HealthCheckable]) async {
        while !Task.isCancelled {
            await checkOnce(entities)
            try? await Task.sleep(nanoseconds: UInt64(max(interval, 0) * 1_000_000_000))
        }
    }
}
