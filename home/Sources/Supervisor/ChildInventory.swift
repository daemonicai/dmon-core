import Foundation

/// The full child inventory (PRD §2.2/§6, design D6): six supervised children plus
/// four read-only monitors. Only `networkGateway` is enabled by this change (task
/// 4.7) — the rest are expressible now so later phases add no model change.
public enum ChildInventory {
    public static let networkGateway = ChildDescriptor(
        id: "network-gateway",
        displayName: "Network Gateway",
        transport: .loopbackHTTP,
        endpoint: URL(string: "http://127.0.0.1:5500")!,
        healthCheck: .http(URL(string: "http://127.0.0.1:5500")!),
        healthCheckTimeout: 5,
        startupOrder: 0,
        adoptionPolicy: .adoptOrSpawn,
        launch: ChildLaunch(candidates: [
            .environmentVariable("DMON_NETWORK_PATH"),
            .homeRelativePath(".dotnet/tools/ndmon")
        ]),
        isEnabled: true
    )

    public static let dcal = ChildDescriptor(
        id: "dcal",
        displayName: "Dcal",
        transport: .loopbackHTTP,
        endpoint: URL(string: "http://localhost:5280")!,
        healthCheck: .http(URL(string: "http://localhost:5280/health")!),
        healthCheckTimeout: 5,
        startupOrder: 1,
        adoptionPolicy: .adoptOrSpawn,
        // dmonium has no default path for Dcal either — only the env override.
        launch: ChildLaunch(candidates: [.environmentVariable("DMON_DCAL_SERVER_PATH")]),
        isEnabled: false
    )

    public static let dmail = ChildDescriptor(
        id: "dmail",
        displayName: "Dmail",
        transport: .loopbackHTTP,
        endpoint: URL(string: "http://127.0.0.1:8080")!,
        healthCheck: .http(URL(string: "http://127.0.0.1:8080/health")!),
        healthCheckTimeout: 5,
        startupOrder: 2,
        adoptionPolicy: .adoptOrSpawn,
        // dmonium has no default path for Dmail either — only the env override.
        launch: ChildLaunch(candidates: [.environmentVariable("DMON_DMAIL_SERVER_PATH")]),
        isEnabled: false
    )

    /// No launch candidates: ADR-034 runs mlx from a uv venv, not a bare command
    /// on `PATH`, so naming one here would assert a resolution path that does
    /// not exist. An empty candidate list is itself meaningful data.
    public static let mlxTriage = ChildDescriptor(
        id: "mlx-triage",
        displayName: "mlx Triage",
        transport: .socket,
        endpoint: URL(string: "http://127.0.0.1:8800")!,
        healthCheck: .http(URL(string: "http://127.0.0.1:8800")!),
        healthCheckTimeout: 5,
        startupOrder: 3,
        adoptionPolicy: .adoptOrSpawn,
        launch: ChildLaunch(arguments: ["--port", "8800"]),
        isEnabled: false
    )

    /// See `mlxTriage`: no launch candidates, same ADR-034 uv-venv reason.
    public static let mlxReasoner = ChildDescriptor(
        id: "mlx-reasoner",
        displayName: "mlx Reasoner",
        transport: .socket,
        endpoint: URL(string: "http://127.0.0.1:8810")!,
        healthCheck: .http(URL(string: "http://127.0.0.1:8810")!),
        healthCheckTimeout: 5,
        startupOrder: 4,
        adoptionPolicy: .adoptOrSpawn,
        launch: ChildLaunch(arguments: ["--port", "8810"]),
        isEnabled: false
    )

    /// Port is provisional and there are no launch candidates: the sidecar has no
    /// implementation yet (design D7). Recorded so the descriptor shape is proven
    /// before Phase 3 needs it.
    public static let speechSidecar = ChildDescriptor(
        id: "speech-sidecar",
        displayName: "Speech Sidecar",
        transport: .socket,
        endpoint: URL(string: "http://127.0.0.1:8820")!,
        healthCheck: .http(URL(string: "http://127.0.0.1:8820")!),
        healthCheckTimeout: 5,
        startupOrder: 5,
        adoptionPolicy: .adoptOrSpawn,
        launch: ChildLaunch(),
        isEnabled: false
    )

    /// The six supervised children, in declared startup order.
    public static let children: [ChildDescriptor] = [
        networkGateway, dcal, dmail, mlxTriage, mlxReasoner, speechSidecar
    ]

    public static let tailscale = MonitorDescriptor(
        id: "tailscale",
        displayName: "Tailscale",
        healthCheck: .process(executablePath: "tailscale", arguments: ["status", "--json"]),
        healthCheckTimeout: 5
    )

    public static let calendarSync = MonitorDescriptor(
        id: "calendar-sync",
        displayName: "Calendar Sync",
        healthCheck: .http(URL(string: "http://localhost:5280/health")!),
        healthCheckTimeout: 5
    )

    public static let mail = MonitorDescriptor(
        id: "mail",
        displayName: "Mail",
        healthCheck: .http(URL(string: "http://127.0.0.1:8080/health")!),
        healthCheckTimeout: 5
    )

    public static let egress = MonitorDescriptor(
        id: "egress",
        displayName: "Egress Endpoint",
        healthCheck: .http(URL(string: "https://generativelanguage.googleapis.com")!),
        healthCheckTimeout: 5
    )

    /// The four read-only monitors.
    public static let monitors: [MonitorDescriptor] = [tailscale, calendarSync, mail, egress]
}
