/// Publishes each supervised entity's current `ChildHealth`, keyed by
/// `ChildID`, so a UI (a later block, section 5) can observe changes without
/// polling the health-check loop directly.
///
/// An `actor` rather than an `@MainActor` object: the health-check loop that
/// writes into this store runs off the main actor so concurrent checks are
/// not serialised through it, and an actor gives every read and write
/// data-race safety without a lock.
public actor ChildHealthStore {
    public typealias Snapshot = [ChildID: ChildHealth]

    private var snapshot: Snapshot = [:]
    private var subscribers: [Int: AsyncStream<Snapshot>.Continuation] = [:]
    private var nextSubscriberToken = 0

    public init() {}

    /// The current health of every child observed so far. A child never
    /// checked has no entry — callers that need `.unknown` for an unobserved
    /// child should use `health(for:)` instead.
    public func currentSnapshot() -> Snapshot {
        snapshot
    }

    /// `.unknown` for a child that has never been checked, rather than an
    /// absent-key crash or a silently misleading default elsewhere.
    public func health(for id: ChildID) -> ChildHealth {
        snapshot[id] ?? .unknown
    }

    /// Records `health` for `id` and notifies every subscriber of the new
    /// snapshot. A no-op write (the same health republished) still notifies —
    /// the store does not assume its callers deduplicate.
    public func publish(_ health: ChildHealth, for id: ChildID) {
        snapshot[id] = health
        for subscriber in subscribers.values {
            subscriber.yield(snapshot)
        }
    }

    /// A live feed of snapshots: the current one immediately, then one per
    /// subsequent `publish(_:for:)` call. Each call opens an independent
    /// subscription; ending iteration (or letting the stream deinitialise)
    /// unregisters it.
    ///
    /// Buffering is explicitly `.unbounded`: a subscriber that is briefly slow
    /// to consume (e.g. a UI mid-render) must see every snapshot in order
    /// rather than have one dropped, and a supervised inventory of a handful
    /// of children can never produce enough snapshots to make that a memory
    /// concern.
    public func updates() -> AsyncStream<Snapshot> {
        let token = nextSubscriberToken
        nextSubscriberToken += 1
        return AsyncStream(bufferingPolicy: .unbounded) { continuation in
            subscribers[token] = continuation
            continuation.yield(snapshot)
            continuation.onTermination = { [weak self] _ in
                // `onTermination` runs synchronously, outside actor isolation,
                // so it cannot call the actor-isolated `removeSubscriber`
                // directly. Hopping through an unstructured `Task` is the
                // standard bridge back onto the actor here: it performs one
                // bounded actor hop with no cancellation to go wrong, not an
                // unsupervised background operation.
                Task { await self?.removeSubscriber(token) }
            }
        }
    }

    private func removeSubscriber(_ token: Int) {
        subscribers.removeValue(forKey: token)
    }
}
