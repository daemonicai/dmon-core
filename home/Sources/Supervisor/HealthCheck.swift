import Foundation

/// How a child's or monitor's liveness is verified.
///
/// Declared rather than closured: a closure field would make descriptors
/// un-`Equatable` and un-inspectable. Execution belongs to the supervisor loop,
/// not the model — this type only says what to check, never how to run it.
public enum HealthCheck: Hashable, Sendable {
    /// An HTTP GET against `url`. Any response, including 4xx/5xx, counts as
    /// reachable; only a connection failure or timeout does not.
    case http(URL)

    /// A raw TCP connection attempt to `host:port`.
    case tcp(host: String, port: Int)

    /// Running `executablePath arguments...` and inspecting its outcome. Used for
    /// monitors with no network endpoint of their own, e.g. the `tailscale` CLI.
    case process(executablePath: String, arguments: [String])

    /// No health check is performed.
    case none
}
