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
    /// Human-readable component name, e.g. "Network", "Dcal", "Tailscale".
    let name: String
    let status: HealthStatus
    /// Optional detail string for display (e.g. error description, exit code).
    let detail: String?
    /// When this snapshot was produced from live state; nil means never published.
    let lastUpdated: Date?

    init(name: String, status: HealthStatus, detail: String? = nil, lastUpdated: Date? = nil) {
        self.name = name
        self.status = status
        self.detail = detail
        self.lastUpdated = lastUpdated
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

/// Classifies a Dmail HTTP health poll result into a `HealthStatus`.
/// Rule: 2xx response (didSucceed=true) → ok; anything else / unreachable → down.
/// This is the opposite of the endpoint rule — a non-2xx Dmail response means down.
func dmailHealth(didSucceed: Bool) -> HealthStatus {
    didSucceed ? .ok : .down
}

/// Classifies a generic inference-endpoint reachability probe into a `HealthStatus`.
/// Rule: any HTTP response (including 4xx/5xx) → ok; connection failure/timeout → down.
/// "didRespond" means any `HTTPURLResponse` was received regardless of status code.
func endpointHealth(didRespond: Bool) -> HealthStatus {
    didRespond ? .ok : .down
}
