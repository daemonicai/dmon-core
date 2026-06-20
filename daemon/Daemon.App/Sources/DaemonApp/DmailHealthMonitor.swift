import Foundation

@MainActor
final class DmailHealthMonitor: ObservableObject {

    /// Mail-server HTTP reachability snapshot for the registry, distinct from the Dmail
    /// *server process* component (which comes from `ServiceManager.makeDmail()`).
    ///
    /// Classification:
    ///   never polled → unknown (initial; amber in rollup — not red)
    ///   last fetch returned 2xx → ok
    ///   last fetch failed or returned non-2xx → down
    @Published private(set) var componentHealth: ComponentHealth =
        ComponentHealth(name: "Mail", status: .unknown)

    private var pollTask: Task<Void, Never>?

    // MARK: - Environment

    private var baseURL: String? {
        ProcessInfo.processInfo.environment["DMAIL_BASE_URL"]
    }

    private var apiKey: String? {
        ProcessInfo.processInfo.environment["DMAIL_API_KEY"]
    }

    // MARK: - Public API

    func start() {
        guard pollTask == nil else { return }
        pollTask = Task.detached(priority: .background) { [weak self] in
            while !Task.isCancelled {
                let succeeded = await self?.fetchHealth() ?? false
                await MainActor.run { [weak self] in
                    self?.applyFetchResult(succeeded)
                }
                try? await Task.sleep(nanoseconds: 30_000_000_000) // 30s
            }
        }
    }

    func stop() {
        pollTask?.cancel()
        pollTask = nil
    }

    // MARK: - HTTP helper

    private func makeRequest() -> URLRequest? {
        guard let base = baseURL, let url = URL(string: "\(base)/health") else { return nil }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.timeoutInterval = 5
        if let key = apiKey {
            request.setValue(key, forHTTPHeaderField: "X-Api-Key")
        }
        return request
    }

    private func fetchHealth() async -> Bool {
        guard let request = makeRequest() else { return false }
        do {
            let (_, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse else { return false }
            return (200..<300).contains(http.statusCode)
        } catch {
            return false
        }
    }

    // MARK: - State update

    private func applyFetchResult(_ succeeded: Bool) {
        componentHealth = ComponentHealth(name: "Mail", status: dmailHealth(didSucceed: succeeded))
    }
}
