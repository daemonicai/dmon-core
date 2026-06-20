import Foundation
import Combine

@MainActor
final class GatewayManager: ObservableObject {

    @Published private(set) var isRunning: Bool = false
    @Published private(set) var lastExitCode: Int32?

    // Settable override for the Gateway binary path (§10 wires this from Settings).
    var gatewayPathOverride: String? {
        didSet { manager.config.executableCandidates = Self.gatewayCandidates(override: gatewayPathOverride) }
    }

    // Settings-panel values surfaced to the core via env on (re)launch (§10.3).
    var settingsEnvironment: [String: String] = [:] {
        didSet { manager.additionalEnvironment = settingsEnvironment }
    }

    private let manager: ServerProcessManager

    init() {
        let pidFileURL = URL(fileURLWithPath: NSHomeDirectory())
            .appendingPathComponent(".dmon/run/gateway.pid")

        let config = ServerProcessConfig(
            displayName: "Gateway",
            executableCandidates: Self.gatewayCandidates(override: nil),
            arguments: ["--agent", "daemon/Daemon.cs"],
            pidFileURL: pidFileURL
        )
        manager = ServerProcessManager(config: config)

        // Mirror the manager's published properties so consumers of GatewayManager
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

    // Priority: override → DMON_GATEWAY_PATH env → ~/.dotnet/tools/Dmon.Gateway
    private static func gatewayCandidates(override: String?) -> [() -> String?] {
        [
            { override },
            { ProcessInfo.processInfo.environment["DMON_GATEWAY_PATH"] },
            { (NSHomeDirectory() as NSString).appendingPathComponent(".dotnet/tools/Dmon.Gateway") }
        ]
    }
}
