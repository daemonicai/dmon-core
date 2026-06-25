import Foundation
import Combine

@MainActor
final class NetworkManager: ObservableObject {

    @Published private(set) var isRunning: Bool = false
    @Published private(set) var lastExitCode: Int32?

    /// Honest process-health snapshot for the registry (ok / down / unknown).
    /// The Network's SPECIAL ICON ROLE (stopped → red) is handled by the
    /// rollup's `networkStopped` parameter, NOT by mapping this to `down`.
    @Published private(set) var componentHealth: ComponentHealth =
        ComponentHealth(name: "Network", status: .unknown)

    // Settable override for the Network binary path (§10 wires this from Settings).
    var networkPathOverride: String? {
        didSet { manager.config.executableCandidates = Self.networkCandidates(override: networkPathOverride) }
    }

    // Settings-panel values surfaced to the core via env on (re)launch (§10.3).
    var settingsEnvironment: [String: String] = [:] {
        didSet { manager.additionalEnvironment = settingsEnvironment }
    }

    private let manager: ServerProcessManager

    init() {
        let pidFileURL = URL(fileURLWithPath: NSHomeDirectory())
            .appendingPathComponent(".dmon/run/network.pid")

        let config = ServerProcessConfig(
            displayName: "Network",
            executableCandidates: Self.networkCandidates(override: nil),
            arguments: ["--agent", "daemon/Daemon.cs"],
            pidFileURL: pidFileURL
        )
        manager = ServerProcessManager(config: config)

        // Mirror the manager's published properties so consumers of NetworkManager
        // observe changes through the standard ObservableObject mechanism.
        manager.$isRunning
            .receive(on: RunLoop.main)
            .assign(to: &$isRunning)
        manager.$lastExitCode
            .receive(on: RunLoop.main)
            .assign(to: &$lastExitCode)

        // Derive componentHealth from isRunning + lastExitCode.
        Publishers.CombineLatest(manager.$isRunning, manager.$lastExitCode)
            .receive(on: RunLoop.main)
            .map { running, exitCode in
                ComponentHealth(
                    name: "Network",
                    status: processHealth(isRunning: running, lastExitCode: exitCode),
                    detail: exitCode.map { "exit \($0)" },
                    lastUpdated: Date()
                )
            }
            .assign(to: &$componentHealth)
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

    // Priority: override → DMON_NETWORK_PATH env → ~/.dotnet/tools/ndmon
    private static func networkCandidates(override: String?) -> [() -> String?] {
        [
            { override },
            { ProcessInfo.processInfo.environment["DMON_NETWORK_PATH"] },
            { (NSHomeDirectory() as NSString).appendingPathComponent(".dotnet/tools/ndmon") }
        ]
    }
}
