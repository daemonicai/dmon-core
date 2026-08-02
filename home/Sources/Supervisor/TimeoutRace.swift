import Foundation

/// Races `operation` against a `timeout`-second deadline, returning
/// `operation`'s value if it finishes first, or `nil` if the deadline elapses
/// first.
///
/// The bound is enforced here, around the seam — never inside `operation`.
/// Whichever side loses the race is cancelled and awaited before this
/// function returns, so a timed-out `operation` never keeps running
/// unsupervised: cooperative cancellation (as `Task.sleep` and `URLSession`'s
/// async APIs both honour) is what lets the loser actually stop.
func withTimeout<T: Sendable>(
    _ timeout: TimeInterval,
    operation: @escaping @Sendable () async -> T
) async -> T? {
    await withTaskGroup(of: T?.self, returning: T?.self) { group in
        group.addTask {
            await operation()
        }
        group.addTask {
            try? await Task.sleep(nanoseconds: UInt64(max(timeout, 0) * 1_000_000_000))
            return nil
        }

        let result = await group.next() ?? nil
        group.cancelAll()
        return result
    }
}
