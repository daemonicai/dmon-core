import Foundation

/// A read-only health source — Tailscale, calendar sync, mail, or egress — that the
/// host observes but never spawns, adopts, or kills.
///
/// This type has no launch, adoption-policy, or startup-order facet at all: there
/// is no field for a monitor to be spawned, adopted, or killed through, so that
/// illegal state is unrepresentable rather than merely discouraged by a `Bool`
/// every call site would otherwise have to remember to check.
public struct MonitorDescriptor: Hashable, Sendable {
    public let id: ChildID
    public let displayName: String
    public let healthCheck: HealthCheck
    public let healthCheckTimeout: TimeInterval

    public init(
        id: ChildID,
        displayName: String,
        healthCheck: HealthCheck,
        healthCheckTimeout: TimeInterval
    ) {
        self.id = id
        self.displayName = displayName
        self.healthCheck = healthCheck
        self.healthCheckTimeout = healthCheckTimeout
    }
}
