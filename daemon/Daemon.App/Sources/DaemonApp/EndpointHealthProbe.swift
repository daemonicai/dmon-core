import Foundation

// MARK: - Reachability probe type

/// Returns true if any `HTTPURLResponse` was received (any status code),
/// false on connection failure or timeout. Injected so tests substitute a fake.
typealias EndpointReachabilityProbe = (URL) async -> Bool

// MARK: - Default probe implementation

/// Performs a GET request with a 5-second timeout; treats any HTTP response
/// (including 4xx/5xx) as reachable. Connection errors and timeouts are not reachable.
func defaultEndpointProbe(url: URL) async -> Bool {
    var request = URLRequest(url: url)
    request.httpMethod = "GET"
    request.timeoutInterval = 5
    do {
        let (_, response) = try await URLSession.shared.data(for: request)
        return response is HTTPURLResponse
    } catch {
        return false
    }
}

// MARK: - Per-endpoint monitor

/// Polls a single inference endpoint on a 30-second interval and publishes
/// a `ComponentHealth` snapshot.
///
/// Classification: any HTTP response (incl. 4xx/5xx) → ok; no response → down.
/// If the configured URL is not a valid URL the component stays `.down` permanently.
///
/// The probe closure is injectable so group-8 tests can substitute a fake
/// without network I/O.
@MainActor
final class EndpointHealthProbe: ObservableObject {

    @Published private(set) var componentHealth: ComponentHealth

    private let name: String
    private let url: URL?
    private let probe: EndpointReachabilityProbe
    private var pollTask: Task<Void, Never>?

    /// - Parameters:
    ///   - name: The `ComponentHealth.name` used verbatim in the registry row.
    ///   - url: The endpoint URL to probe; pass nil (or a URL built from a bad string)
    ///          to have the component immediately report `.down`.
    ///   - probe: I/O closure. Defaults to a real URLSession GET; swap for tests.
    init(name: String, url: URL?, probe: @escaping EndpointReachabilityProbe = defaultEndpointProbe) {
        self.name = name
        self.url = url
        self.probe = probe
        self.componentHealth = ComponentHealth(name: name, status: .unknown)
    }

    // MARK: - Public API

    func start() {
        guard pollTask == nil else { return }
        let name = self.name
        let url = self.url
        let probe = self.probe
        pollTask = Task.detached(priority: .background) { [weak self] in
            while !Task.isCancelled {
                let responded: Bool
                if let url {
                    responded = await probe(url)
                } else {
                    responded = false
                }
                await MainActor.run { [weak self] in
                    self?.componentHealth = ComponentHealth(
                        name: name,
                        status: endpointHealth(didRespond: responded)
                    )
                }
                try? await Task.sleep(nanoseconds: 30_000_000_000) // 30s
            }
        }
    }

    func stop() {
        pollTask?.cancel()
        pollTask = nil
    }
}
