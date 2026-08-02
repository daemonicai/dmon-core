import Foundation

/// A supervised child, expressed entirely as configuration.
///
/// Every child the host will eventually own — the network gateway, `Dcal`,
/// `Dmail`, the mlx reasoner, the mlx triage head, and the speech sidecar — is one
/// value of this type. Adding a child never requires a new type, a subclass, or a
/// `switch` over kind: it requires a new value.
public struct ChildDescriptor: Hashable, Sendable {
    public let id: ChildID
    public let displayName: String
    public let transport: ChildTransport
    public let endpoint: URL
    public let healthCheck: HealthCheck
    public let healthCheckTimeout: TimeInterval
    public let startupOrder: Int
    public let adoptionPolicy: AdoptionPolicy
    public let launch: ChildLaunch

    /// Whether the host brings this child up today. Read by later blocks instead
    /// of a comment, so it survives past the OpenSpec change that introduced it —
    /// only the network gateway is enabled; the rest are inventory the model
    /// already accommodates.
    public let isEnabled: Bool

    public init(
        id: ChildID,
        displayName: String,
        transport: ChildTransport,
        endpoint: URL,
        healthCheck: HealthCheck,
        healthCheckTimeout: TimeInterval,
        startupOrder: Int,
        adoptionPolicy: AdoptionPolicy,
        launch: ChildLaunch,
        isEnabled: Bool
    ) {
        self.id = id
        self.displayName = displayName
        self.transport = transport
        self.endpoint = endpoint
        self.healthCheck = healthCheck
        self.healthCheckTimeout = healthCheckTimeout
        self.startupOrder = startupOrder
        self.adoptionPolicy = adoptionPolicy
        self.launch = launch
        self.isEnabled = isEnabled
    }
}
