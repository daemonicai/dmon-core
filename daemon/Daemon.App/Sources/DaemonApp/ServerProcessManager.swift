import Foundation
import Combine

// Configuration for a supervised server process.
// Group 3 constructs one instance per additional server (Dcal, Dmail).
struct ServerProcessConfig {
    // Human-readable name used in logging.
    let displayName: String
    // Ordered list of candidate executable paths; first executable file wins.
    // Closures allow lazy / override-at-runtime resolution (e.g. from Settings).
    var executableCandidates: [() -> String?]
    // Arguments passed verbatim to the executable.
    let arguments: [String]
    // PID file used for adopt-on-start and cleanup.
    let pidFileURL: URL
    // Base environment entries injected at launch (merged over inherited env).
    // Mutable: callers may append keys before each start (e.g. GatewayManager
    // sets settingsEnvironment here before delegating to start()).
    var baseEnvironment: [String: String] = [:]
}

// Injectable liveness probe. Defaults to `kill(pid, 0) == 0`.
// Group 8 / XCTest injects a closure that drives live-vs-dead without a real process.
typealias PidLivenessProbe = (_ pid: Int32) -> Bool

@MainActor
final class ServerProcessManager: ObservableObject {

    @Published private(set) var isRunning: Bool = false
    @Published private(set) var lastExitCode: Int32?

    var config: ServerProcessConfig
    // Mutable so GatewayManager can point it at the current settingsEnvironment.
    var additionalEnvironment: [String: String] = [:]

    private var process: Process?
    private var currentBackoff: TimeInterval = 2
    private var intentionalStop: Bool = false

    // Seam (2.3): injectable so tests can drive liveness without a real process.
    var livenessProbe: PidLivenessProbe

    init(config: ServerProcessConfig, livenessProbe: @escaping PidLivenessProbe = { kill($0, 0) == 0 }) {
        self.config = config
        self.livenessProbe = livenessProbe
    }

    // MARK: - Public API

    func start() {
        if adoptIfRunning() {
            isRunning = true
            return
        }

        guard let url = resolveExecutableURL() else {
            isRunning = false
            return
        }

        let p = Process()
        p.executableURL = url
        p.arguments = config.arguments

        // Merge: inherited env → base config env → caller-supplied additional env.
        // Later entries win; additionalEnvironment (settingsEnvironment) has highest priority.
        let merged = config.baseEnvironment.merging(additionalEnvironment) { _, new in new }
        if !merged.isEmpty {
            p.environment = ProcessInfo.processInfo.environment.merging(merged) { _, new in new }
        }

        p.terminationHandler = { [weak self] terminatedProcess in
            // terminationHandler fires on an arbitrary background thread.
            Task { @MainActor [weak self] in
                self?.handleTermination(exitCode: terminatedProcess.terminationStatus)
            }
        }

        do {
            try p.run()
        } catch {
            isRunning = false
            return
        }

        process = p
        writePIDFile(pid: p.processIdentifier)

        // Back-off resets to 2s after every successful start.
        currentBackoff = 2
        isRunning = true
    }

    func stop() {
        intentionalStop = true
        process?.terminate()
        process = nil
        clearPIDFile()
        isRunning = false
    }

    func restart() {
        stop()
        start()
    }

    // MARK: - Termination / back-off

    private func handleTermination(exitCode: Int32) {
        lastExitCode = exitCode
        isRunning = false

        guard !intentionalStop else {
            intentionalStop = false
            return
        }

        let delay = currentBackoff
        // Advance back-off: ×2, cap at 60s.
        currentBackoff = min(currentBackoff * 2, 60)

        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            self?.start()
        }
    }

    // MARK: - Executable resolution

    private func resolveExecutableURL() -> URL? {
        for candidate in config.executableCandidates {
            guard let path = candidate() else { continue }
            let url = URL(fileURLWithPath: path)
            if FileManager.default.isExecutableFile(atPath: url.path) {
                return url
            }
        }
        return nil
    }

    // MARK: - PID file

    /// Returns `true` if a live process was adopted from the PID file.
    func adoptIfRunning() -> Bool {
        guard let pidData = try? Data(contentsOf: config.pidFileURL),
              let pidString = String(data: pidData, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              let pid = Int32(pidString) else {
            return false
        }
        return livenessProbe(pid)
    }

    private func writePIDFile(pid: Int32) {
        let dir = config.pidFileURL.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let data = Data("\(pid)".utf8)
        try? data.write(to: config.pidFileURL)
    }

    private func clearPIDFile() {
        try? FileManager.default.removeItem(at: config.pidFileURL)
    }
}
