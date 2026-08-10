/// The observed health of a supervised child process.
///
/// Exactly the three cases `HealthChecker.check(_:timeout:)` can produce —
/// `ChildHealthStore`'s sole writer, via `HealthMonitor.checkOnce`, only
/// ever forwards what that returns. A case with no writer would be
/// unreachable observation surface rather than a genuine state.
public enum ChildHealth: String, Hashable, Sendable {
    /// No health signal has been observed yet.
    case unknown

    /// The child is running and reporting readiness.
    case healthy

    /// The child is running but reporting failure, or has stopped responding.
    case unhealthy
}
