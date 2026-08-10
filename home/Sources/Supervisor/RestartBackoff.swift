import Foundation

/// The exponential-delay sequence applied between restart attempts after a
/// supervised child exits unexpectedly.
///
/// Reset is keyed on the child having **stayed up** past `stabilityThreshold`
/// before it exited, never on a restart attempt merely having launched
/// successfully. dmonium's `ServerProcessManager` resets `currentBackoff` to
/// its initial value after every successful `start()` (line 90), while
/// `handleTermination` doubles it on each crash — so a child that launches
/// fine and dies immediately produces start (reset) → crash → wait → start
/// (reset) → crash → wait, forever at the initial delay, never escalating.
/// The doubling only ever fires when `start()` itself fails to launch, which
/// is the less common crash-loop shape. Keying reset on uptime instead means
/// a crash loop escalates regardless of how quickly each restart attempt
/// itself succeeds.
public struct RestartBackoff: Sendable {
    public let initial: TimeInterval
    public let maximum: TimeInterval

    /// How long a child must run before its next exit is treated as a fresh
    /// failure rather than a continuation of a crash loop.
    public let stabilityThreshold: TimeInterval

    private var currentDelay: TimeInterval

    public init(initial: TimeInterval = 2, maximum: TimeInterval = 60, stabilityThreshold: TimeInterval = 30) {
        precondition(initial > 0, "initial delay must be positive")
        precondition(maximum >= initial, "maximum must be at least initial")
        self.initial = initial
        self.maximum = maximum
        self.stabilityThreshold = stabilityThreshold
        self.currentDelay = initial
    }

    /// Records that the child ran for `uptime` seconds before this exit, and
    /// returns the delay to wait before the next restart attempt.
    ///
    /// If `uptime` met or exceeded `stabilityThreshold`, the sequence resets
    /// to `initial` **before** this delay is read — so a child that just
    /// proved itself stable gets the initial delay on its very next restart,
    /// and only escalates again if that restart also fails quickly. Otherwise
    /// the delay doubles (capped at `maximum`) for whichever exit comes next.
    public mutating func nextRestartDelay(afterUptime uptime: TimeInterval) -> TimeInterval {
        if uptime >= stabilityThreshold {
            currentDelay = initial
        }
        let delay = currentDelay
        currentDelay = min(currentDelay * 2, maximum)
        return delay
    }

    /// Whether `delay` (as returned by `nextRestartDelay`) has reached the
    /// cap — the signal callers use to distinguish "still backing off" from
    /// "repeated failure".
    public func hasReachedMaximum(_ delay: TimeInterval) -> Bool {
        delay >= maximum
    }
}
