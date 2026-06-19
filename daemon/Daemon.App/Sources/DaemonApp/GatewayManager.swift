import Foundation
import Combine

@MainActor
final class GatewayManager: ObservableObject {

    @Published private(set) var isRunning: Bool = false
    @Published private(set) var lastExitCode: Int32?

    // Settable override for the Gateway binary path (§10 wires this from Settings).
    var gatewayPathOverride: String?

    private var process: Process?
    private var currentBackoff: TimeInterval = 2
    // Prevents terminationHandler from scheduling a restart on an intentional stop.
    private var intentionalStop: Bool = false

    // MARK: - Public API

    func start() {
        // 7.3: Adopt a live process from PID file rather than spawning a new one.
        if adoptIfRunning() {
            isRunning = true
            return
        }

        guard let url = resolveGatewayURL() else {
            isRunning = false
            return
        }

        let p = Process()
        p.executableURL = url
        p.arguments = ["--agent", "daemon/Daemon.cs"]

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

    // MARK: - Termination / back-off (7.2)

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

    // MARK: - Binary path resolution (7.4)

    private func resolveGatewayURL() -> URL? {
        // Priority: override → DMON_GATEWAY_PATH env → ~/.dotnet/tools/Dmon.Gateway
        let candidates: [String] = [
            gatewayPathOverride,
            ProcessInfo.processInfo.environment["DMON_GATEWAY_PATH"],
            (NSHomeDirectory() as NSString).appendingPathComponent(".dotnet/tools/Dmon.Gateway")
        ].compactMap { $0 }

        for path in candidates {
            let url = URL(fileURLWithPath: path)
            if FileManager.default.isExecutableFile(atPath: url.path) {
                return url
            }
        }
        return nil
    }

    // MARK: - PID file (7.3)

    private var pidFileURL: URL {
        URL(fileURLWithPath: NSHomeDirectory())
            .appendingPathComponent(".dmon/run/gateway.pid")
    }

    /// Returns `true` if a live process was adopted from the PID file.
    private func adoptIfRunning() -> Bool {
        guard let pidData = try? Data(contentsOf: pidFileURL),
              let pidString = String(data: pidData, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              let pid = Int32(pidString) else {
            return false
        }
        // kill(pid, 0): returns 0 if alive, -1 with errno ESRCH if dead.
        return kill(pid, 0) == 0
    }

    private func writePIDFile(pid: Int32) {
        let dir = pidFileURL.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let data = Data("\(pid)".utf8)
        try? data.write(to: pidFileURL)
    }

    private func clearPIDFile() {
        try? FileManager.default.removeItem(at: pidFileURL)
    }
}
