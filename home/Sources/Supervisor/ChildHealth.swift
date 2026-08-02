/// The observed health of a supervised child process.
public enum ChildHealth: String, Hashable, Sendable {
    /// No health signal has been observed yet.
    case unknown

    /// The child has been launched but has not yet reported readiness.
    case starting

    /// The child is running and reporting readiness.
    case healthy

    /// The child is running but reporting failure, or has stopped responding.
    case unhealthy

    /// The child process has exited.
    case stopped

    /// Whether the child is expected to be doing useful work right now.
    public var isRunning: Bool {
        switch self {
        case .starting, .healthy, .unhealthy:
            return true
        case .unknown, .stopped:
            return false
        }
    }
}
