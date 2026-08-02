/// How a running child is reached, once it has been adopted or spawned.
public enum ChildTransport: Hashable, Sendable {
    /// An HTTP server bound to loopback, reached at its declared endpoint.
    case loopbackHTTP

    /// A local socket server (e.g. a model-serving process) reached at its declared endpoint.
    case socket
}
