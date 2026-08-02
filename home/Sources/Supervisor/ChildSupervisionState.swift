import Foundation

/// What crash detection currently believes about one supervised child's
/// restart history — the crash-detection analogue of `ChildHealth`, which
/// instead reflects endpoint health-check results. Published so a UI
/// (section 5) can render it without polling `HostSupervisor` directly.
public enum ChildSupervisionState: Hashable, Sendable {
    /// The child has not crashed, or was adopted rather than spawned (so
    /// this host owns no process to detect a crash of in the first place).
    case normal

    /// The child exited unexpectedly and a restart has been scheduled after
    /// `delay` seconds.
    case restarting(delay: TimeInterval)

    /// The child has crashed repeatedly without ever staying up long enough
    /// to be considered stable, and the backoff delay has reached its cap.
    /// Kept distinct from `.restarting` so a UI can distinguish "recovering"
    /// from "this needs a human" — the spec's "surfaces the repeated failure"
    /// half of the requirement.
    case repeatedFailure(delay: TimeInterval)

    /// The host asked this child to stop (graceful shutdown) and it will not
    /// be restarted.
    case stoppedIntentionally
}
