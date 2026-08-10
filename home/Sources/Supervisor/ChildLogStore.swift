import Foundation

/// One child's retained output: its most recent lines, plus how many older
/// lines were dropped to stay within `ChildLogStore`'s per-child cap.
///
/// `droppedCount` exists so a capped, chatty child is distinguishable from a
/// child that genuinely never logged much — a silent drop would otherwise
/// leave both looking identical at the pane.
public struct ChildLogBuffer: Hashable, Sendable {
    public let lines: [ChildLogLine]
    public let droppedCount: Int

    public static let empty = ChildLogBuffer(lines: [], droppedCount: 0)

    public init(lines: [ChildLogLine], droppedCount: Int) {
        self.lines = lines
        self.droppedCount = droppedCount
    }
}

/// Publishes each supervised child's captured stdout/stderr, keyed by
/// `ChildID` — the log-pane analogue of `ChildSupervisionStore`, which
/// publishes restart history rather than raw output.
///
/// An `actor`, for the same reason as `ChildSupervisionStore`: the reader
/// tasks draining independent pipes for independent (and independently
/// restarting) children all append here concurrently, and an actor gives
/// every append and read data-race safety without a lock.
///
/// **Never cleared** — not on restart, crash, or shutdown. Retention across
/// a restart is the point (Requirement: Child process output is streamed to
/// a log pane, scenario "Output survives a child restart"): a new
/// generation of a child appends to the same buffer its previous generation
/// wrote into, keyed by the `ChildID` that outlives any one process
/// generation.
///
/// Bounded per child at `capacityPerChild` lines (default
/// `defaultCapacityPerChild`, 1000): a host left running for days must not
/// grow this without limit, and 1000 lines is generous for diagnosing a
/// recent crash without needing every line a chatty child has ever written
/// since launch. Dropping the oldest line(s) past the cap is silent to
/// nothing but the count — see `ChildLogBuffer.droppedCount`.
public actor ChildLogStore {
    public typealias Snapshot = [ChildID: ChildLogBuffer]

    public static let defaultCapacityPerChild = 1000

    private let capacityPerChild: Int
    private var snapshot: Snapshot = [:]
    private var nextLineID = 0
    private var subscribers: [Int: AsyncStream<Snapshot>.Continuation] = [:]
    private var nextSubscriberToken = 0

    public init(capacityPerChild: Int = ChildLogStore.defaultCapacityPerChild) {
        self.capacityPerChild = capacityPerChild
    }

    /// Every child's captured output observed so far.
    public func currentSnapshot() -> Snapshot {
        snapshot
    }

    /// `.empty` for a child with no captured output yet, rather than an
    /// absent-key crash or a silently misleading default elsewhere.
    public func buffer(for id: ChildID) -> ChildLogBuffer {
        snapshot[id] ?? .empty
    }

    /// Appends one line to `id`'s buffer, dropping the oldest line first if
    /// this would exceed `capacityPerChild`, and notifies every subscriber
    /// of the new snapshot.
    public func append(_ text: String, source: ChildLogSource, for id: ChildID, capturedAt: Date = Date()) {
        var buffer = snapshot[id] ?? .empty
        var lines = buffer.lines
        lines.append(ChildLogLine(id: nextLineID, childID: id, source: source, text: text, capturedAt: capturedAt))
        nextLineID += 1

        var droppedCount = buffer.droppedCount
        if lines.count > capacityPerChild {
            let overflow = lines.count - capacityPerChild
            lines.removeFirst(overflow)
            droppedCount += overflow
        }

        buffer = ChildLogBuffer(lines: lines, droppedCount: droppedCount)
        snapshot[id] = buffer
        for subscriber in subscribers.values {
            subscriber.yield(snapshot)
        }
    }

    /// A live feed of snapshots: the current one immediately, then one per
    /// subsequent `append(_:source:for:capturedAt:)` call. Mirrors
    /// `ChildSupervisionStore.updates()`.
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
