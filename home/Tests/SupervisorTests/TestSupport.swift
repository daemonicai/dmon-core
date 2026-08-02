import Foundation
@testable import Supervisor

/// Polls `condition` until it is true or `timeout` elapses.
///
/// Checks `Task.isCancelled` itself rather than trusting `Task.sleep`'s
/// throw-on-cancellation alone: `try?` swallows that throw, so a loop
/// without its own check would keep polling forever once `withTimeout`'s
/// losing race cancels it — bounded only by however long `condition` takes
/// to become true, which is exactly the "bounded that isn't" defect this
/// section's own fixes were about. Shared here after the identical fix was
/// made independently, twice, in per-file copies of this helper.
func waitUntilTrue(timeout: TimeInterval, condition: @escaping @Sendable () async -> Bool) async -> Bool {
    await withTimeout(timeout) {
        while !Task.isCancelled {
            if await condition() { return true }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        return false
    } ?? false
}

extension AwaitExitOutcome {
    /// Test convenience: the exit status if this outcome is `.exited`, `nil`
    /// otherwise. Kept test-only rather than promoted to the library —
    /// production call sites are expected to `switch` exhaustively rather
    /// than collapse `.cancelled`/`.reapFailed` into a single `nil`.
    var exitedStatus: ChildExitStatus? {
        if case .exited(let status) = self { return status }
        return nil
    }
}
