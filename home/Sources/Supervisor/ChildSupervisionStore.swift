import Foundation

/// Publishes each supervised child's current `ChildSupervisionState`, keyed
/// by `ChildID` — the crash-detection analogue of `ChildHealthStore`, which
/// publishes health-check results rather than restart history.
///
/// An `actor`, for the same reason as `ChildHealthStore`: `HostSupervisor`
/// writes into this from each child's independent restart loop, and an actor
/// gives every read and write data-race safety without a lock.
public actor ChildSupervisionStore {
    public typealias Snapshot = [ChildID: ChildSupervisionState]

    private var snapshot: Snapshot = [:]
    private var subscribers: [Int: AsyncStream<Snapshot>.Continuation] = [:]
    private var nextSubscriberToken = 0

    public init() {}

    /// The current supervision state of every child observed so far. A child
    /// never observed has no entry — callers that need `.normal` for an
    /// unobserved child should use `state(for:)` instead.
    public func currentSnapshot() -> Snapshot {
        snapshot
    }

    /// `.normal` for a child that has never crashed or been stopped, rather
    /// than an absent-key crash or a silently misleading default elsewhere.
    public func state(for id: ChildID) -> ChildSupervisionState {
        snapshot[id] ?? .normal
    }

    /// Records `state` for `id` and notifies every subscriber of the new
    /// snapshot. A no-op write (the same state republished) still notifies —
    /// the store does not assume its callers deduplicate.
    public func publish(_ state: ChildSupervisionState, for id: ChildID) {
        snapshot[id] = state
        for subscriber in subscribers.values {
            subscriber.yield(snapshot)
        }
    }

    /// A live feed of snapshots: the current one immediately, then one per
    /// subsequent `publish(_:for:)` call. Each call opens an independent
    /// subscription; ending iteration (or letting the stream deinitialise)
    /// unregisters it.
    public func updates() -> AsyncStream<Snapshot> {
        let token = nextSubscriberToken
        nextSubscriberToken += 1
        return AsyncStream(bufferingPolicy: .unbounded) { continuation in
            subscribers[token] = continuation
            continuation.yield(snapshot)
            continuation.onTermination = { [weak self] _ in
                Task { await self?.removeSubscriber(token) }
            }
        }
    }

    private func removeSubscriber(_ token: Int) {
        subscribers.removeValue(forKey: token)
    }

    /// Exposed for tests only, to prove `onTermination` actually unregisters
    /// a subscriber rather than leaking its continuation forever.
    var subscriberCountForTesting: Int {
        subscribers.count
    }
}
