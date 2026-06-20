import Foundation
import Combine

// Generic thin wrapper around ServerProcessManager for auxiliary server processes
// (Dcal, Dmail). Mirrors GatewayManager's pattern: ObservableObject that owns a
// ServerProcessManager, exposes a path-override and a settingsEnvironment seam
// for group-7 wiring, and mirrors the manager's published properties via Combine.
//
// Usage: instantiate one per server with a distinct ServiceManagerConfig.
@MainActor
final class ServiceManager: ObservableObject {

    @Published private(set) var isRunning: Bool = false
    @Published private(set) var lastExitCode: Int32?

    // Settable override for the server binary path (group 7 wires this from Settings).
    var pathOverride: String? {
        didSet { manager.config.executableCandidates = Self.candidates(
            envKey: envKey, override: pathOverride
        )}
    }

    // Settings-panel values surfaced to the server via env on (re)launch (group 7).
    // Mirror GatewayManager.settingsEnvironment: routes into additionalEnvironment.
    var settingsEnvironment: [String: String] = [:] {
        didSet { manager.additionalEnvironment = settingsEnvironment }
    }

    let manager: ServerProcessManager

    // The env-var key used for path-override resolution, e.g. "DMON_DCAL_SERVER_PATH".
    private let envKey: String

    // MARK: - Init

    init(config: ServerProcessConfig, envKey: String,
         livenessProbe: @escaping PidLivenessProbe = { kill($0, 0) == 0 }) {
        self.envKey = envKey
        self.manager = ServerProcessManager(config: config, livenessProbe: livenessProbe)

        // Mirror the manager's published properties so consumers of ServiceManager
        // observe changes through the standard ObservableObject mechanism.
        manager.$isRunning
            .receive(on: RunLoop.main)
            .assign(to: &$isRunning)
        manager.$lastExitCode
            .receive(on: RunLoop.main)
            .assign(to: &$lastExitCode)
    }

    // MARK: - Public API

    func start() {
        manager.start()
    }

    func stop() {
        manager.stop()
    }

    func restart() {
        manager.restart()
    }

    // MARK: - Private

    // Priority: override → env-var path → (no default; see note below).
    //
    // A repo-relative default (services/<Name>/bin/Release/net10.0/<Name>) is NOT
    // provided because Daemon.App's process cwd is not the repo root at runtime
    // (macOS sets cwd to "/" for menu-bar apps launched via Finder/login items).
    // Without a reliable anchor we cannot construct a default that will reliably
    // point at the correct binary. If no env var or override is set the manager
    // will not resolve an executable and will not spawn — this is the designed
    // "not configured" terminal state (design.md Decision 2).
    private static func candidates(envKey: String, override: String?) -> [() -> String?] {
        [
            { override },
            { ProcessInfo.processInfo.environment[envKey] }
        ]
    }
}

// MARK: - Factory helpers

extension ServiceManager {

    /// Creates the ServiceManager for the Dcal server.
    static func makeDcal() -> ServiceManager {
        let pidFileURL = URL(fileURLWithPath: NSHomeDirectory())
            .appendingPathComponent(".dmon/run/dcal.pid")

        let config = ServerProcessConfig(
            displayName: "Dcal",
            executableCandidates: candidates(envKey: "DMON_DCAL_SERVER_PATH", override: nil),
            arguments: [],
            pidFileURL: pidFileURL,
            baseEnvironment: [:]  // Dcal reads DCAL_* from inherited env; no static overlays.
        )
        return ServiceManager(config: config, envKey: "DMON_DCAL_SERVER_PATH")
    }

    /// Creates the ServiceManager for the Dmail server.
    static func makeDmail() -> ServiceManager {
        let pidFileURL = URL(fileURLWithPath: NSHomeDirectory())
            .appendingPathComponent(".dmon/run/dmail.pid")

        let config = ServerProcessConfig(
            displayName: "Dmail",
            executableCandidates: candidates(envKey: "DMON_DMAIL_SERVER_PATH", override: nil),
            arguments: [],
            pidFileURL: pidFileURL,
            baseEnvironment: [:]  // Dmail reads DMAIL_* from inherited env; no static overlays.
        )
        return ServiceManager(config: config, envKey: "DMON_DMAIL_SERVER_PATH")
    }
}
