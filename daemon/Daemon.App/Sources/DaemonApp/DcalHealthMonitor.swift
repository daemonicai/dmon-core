import Foundation

@MainActor
final class DcalHealthMonitor: ObservableObject {

    @Published private(set) var lastSync: String?
    @Published private(set) var eventCount: Int?

    private var pollTask: Task<Void, Never>?

    // MARK: - Environment

    private var baseURL: String {
        ProcessInfo.processInfo.environment["DCAL_BASE_URL"] ?? "http://localhost:5280"
    }

    private var apiKey: String? {
        ProcessInfo.processInfo.environment["DCAL_API_KEY"]
    }

    // MARK: - Public API

    func start() {
        guard pollTask == nil else { return }
        pollTask = Task.detached(priority: .background) { [weak self] in
            while !Task.isCancelled {
                let result = await self?.fetchHealth()
                await MainActor.run { [weak self] in
                    self?.lastSync = result?.lastSync ?? nil
                    self?.eventCount = result?.eventCount ?? nil
                }
                try? await Task.sleep(nanoseconds: 30_000_000_000) // 30s
            }
        }
    }

    func stop() {
        pollTask?.cancel()
        pollTask = nil
    }

    func syncNow() async {
        await postSync()
        let result = await fetchHealth()
        lastSync = result?.lastSync ?? nil
        eventCount = result?.eventCount ?? nil
    }

    // MARK: - HTTP helpers

    private struct HealthResponse: Decodable {
        let lastSync: String?
        let eventCount: Int?
    }

    private func makeRequest(path: String, method: String = "GET") -> URLRequest? {
        guard let url = URL(string: "\(baseURL)\(path)") else { return nil }
        var request = URLRequest(url: url)
        request.httpMethod = method
        if let key = apiKey {
            request.setValue(key, forHTTPHeaderField: "X-Api-Key")
        }
        return request
    }

    private func fetchHealth() async -> HealthResponse? {
        guard let request = makeRequest(path: "/health") else { return nil }
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
                return nil
            }
            return try JSONDecoder().decode(HealthResponse.self, from: data)
        } catch {
            return nil
        }
    }

    private func postSync() async {
        guard let request = makeRequest(path: "/api/sync", method: "POST") else { return }
        do {
            let (_, response) = try await URLSession.shared.data(for: request)
            _ = response // 204 No Content; result not used
        } catch {
            // Fail-soft: sync errors are non-fatal.
        }
    }
}
