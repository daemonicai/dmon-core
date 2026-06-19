import Foundation
import Combine

// Authoritative decision table (spec overrides design):
//   binary not found / cannot launch → .degraded  (spec: "not installed" = amber)
//   process ran but error / non-zero / unparseable / BackendState != "Running" → .down
//   BackendState == "Running" + no reachable peers → .degraded
//   BackendState == "Running" + peers present → .up
enum TailscaleStatus {
    case up
    case degraded
    case down
}

@MainActor
final class TailscaleMonitor: ObservableObject {

    @Published private(set) var status: TailscaleStatus = .down

    private var pollTask: Task<Void, Never>?

    // MARK: - Public API (8.1)

    func start() {
        guard pollTask == nil else { return }
        pollTask = Task.detached(priority: .background) { [weak self] in
            while !Task.isCancelled {
                let result = await TailscaleMonitor.poll()
                await MainActor.run { [weak self] in
                    self?.status = result
                }
                try? await Task.sleep(nanoseconds: 30_000_000_000) // 30s
            }
        }
    }

    func stop() {
        pollTask?.cancel()
        pollTask = nil
    }

    // MARK: - Polling (8.2, 8.3)

    /// Runs on a background task; publishes result on the main actor (see call site).
    private static func poll() async -> TailscaleStatus {
        guard let tailscaleURL = resolveTailscaleBinary() else {
            // Binary not found in PATH or common locations → degraded (spec requirement).
            return .degraded
        }

        let pipe = Pipe()
        let p = Process()
        p.executableURL = tailscaleURL
        p.arguments = ["status", "--json"]
        p.standardOutput = pipe
        p.standardError = Pipe() // suppress stderr

        do {
            try p.run()
        } catch {
            // Could not launch → degraded (spec: binary not found / cannot launch).
            return .degraded
        }

        p.waitUntilExit()

        guard p.terminationStatus == 0 else {
            // Non-zero exit → down.
            return .down
        }

        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        return parseStatus(from: data)
    }

    // MARK: - JSON parsing (8.2)

    private struct StatusPayload: Decodable {
        let BackendState: String?
        // "Self" is a reserved keyword in Swift; map with CodingKeys.
        let selfNode: SelfNode?
        let Peer: [String: PeerNode]?

        enum CodingKeys: String, CodingKey {
            case BackendState
            case selfNode = "Self"
            case Peer
        }
    }

    private struct SelfNode: Decodable {}
    private struct PeerNode: Decodable {}

    private static func parseStatus(from data: Data) -> TailscaleStatus {
        guard let payload = try? JSONDecoder().decode(StatusPayload.self, from: data) else {
            return .down
        }
        guard payload.BackendState == "Running" else {
            // Backend is not running → down.
            return .down
        }
        // Running: check for reachable peers.
        let hasPeers = !(payload.Peer ?? [:]).isEmpty
        return hasPeers ? .up : .degraded
    }

    // MARK: - Binary resolution

    /// Searches PATH then common install locations for the `tailscale` CLI.
    private static func resolveTailscaleBinary() -> URL? {
        // Try PATH-based resolution first via `which`.
        if let pathURL = resolveViaWhich("tailscale") {
            return pathURL
        }
        // Common macOS install locations.
        let commonPaths = [
            "/usr/local/bin/tailscale",
            "/Applications/Tailscale.app/Contents/MacOS/Tailscale"
        ]
        for path in commonPaths {
            if FileManager.default.isExecutableFile(atPath: path) {
                return URL(fileURLWithPath: path)
            }
        }
        return nil
    }

    private static func resolveViaWhich(_ name: String) -> URL? {
        let whichPath = "/usr/bin/which"
        guard FileManager.default.isExecutableFile(atPath: whichPath) else { return nil }

        let pipe = Pipe()
        let p = Process()
        p.executableURL = URL(fileURLWithPath: whichPath)
        p.arguments = [name]
        p.standardOutput = pipe
        p.standardError = Pipe()

        guard (try? p.run()) != nil else { return nil }
        p.waitUntilExit()
        guard p.terminationStatus == 0 else { return nil }

        let output = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !output.isEmpty else { return nil }
        return URL(fileURLWithPath: output)
    }
}
