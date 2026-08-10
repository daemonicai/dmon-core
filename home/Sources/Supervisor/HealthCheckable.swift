import Foundation

/// The narrow surface the health-check loop needs from a supervised entity.
///
/// `ChildDescriptor` and `MonitorDescriptor` both conform, so one loop can walk
/// both without a union type — but this protocol exposes only what checking
/// health requires. It deliberately does **not** expose `launch` or
/// `adoptionPolicy`: `MonitorDescriptor` has neither, by design, and widening
/// this surface to accommodate them would make spawning a monitor
/// representable again through the shared type.
public protocol HealthCheckable: Sendable {
    var id: ChildID { get }
    var healthCheck: HealthCheck { get }
    var healthCheckTimeout: TimeInterval { get }
}

extension ChildDescriptor: HealthCheckable {}
extension MonitorDescriptor: HealthCheckable {}
