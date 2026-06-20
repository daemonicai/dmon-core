import Foundation

// MARK: - HealthStatus

/// Severity level for a single monitored component.
enum HealthStatus: Equatable {
    case ok
    case degraded
    case down
    case unknown
}

// MARK: - ComponentHealth

/// Snapshot of one component's health, as published into `HealthRegistry`.
struct ComponentHealth: Equatable {
    /// Human-readable component name, e.g. "Gateway", "Dcal", "Tailscale".
    let name: String
    let status: HealthStatus
    /// Optional detail string for display (e.g. error description, exit code).
    let detail: String?

    init(name: String, status: HealthStatus, detail: String? = nil) {
        self.name = name
        self.status = status
        self.detail = detail
    }
}

// MARK: - Status classifiers (pure functions; testable in group 8)

/// Classifies a supervised-process manager's state into a `HealthStatus`.
/// Rule: running → ok; never launched (no exit code) → unknown; exited → down.
func processHealth(isRunning: Bool, lastExitCode: Int32?) -> HealthStatus {
    if isRunning { return .ok }
    if lastExitCode == nil { return .unknown }
    return .down
}

/// Maps a `TailscaleStatus` to `HealthStatus`.
func tailscaleHealth(status: TailscaleStatus) -> HealthStatus {
    switch status {
    case .up:       return .ok
    case .degraded: return .degraded
    case .down:     return .down
    }
}
