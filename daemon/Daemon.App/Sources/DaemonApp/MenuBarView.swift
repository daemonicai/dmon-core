import SwiftUI

struct MenuBarView: View {

    @EnvironmentObject var gateway: GatewayManager
    @EnvironmentObject var tailscale: TailscaleMonitor
    @EnvironmentObject var health: DcalHealthMonitor

    var body: some View {
        // Status rows
        Text(gateway.isRunning ? "Gateway: Running" : "Gateway: Stopped")
        Text(tailscaleStatusLabel)
        Text(lastSyncLabel)

        Divider()

        // Start/Stop Gateway
        Button(gateway.isRunning ? "Stop Gateway" : "Start Gateway") {
            if gateway.isRunning {
                gateway.stop()
            } else {
                gateway.start()
            }
        }

        // Sync Calendar Now
        Button("Sync Calendar Now") {
            Task {
                await health.syncNow()
            }
        }

        // Open Settings
        SettingsLink {
            Text("Open Settings…")
        }

        // Quit
        Button("Quit") {
            NSApplication.shared.terminate(nil)
        }
    }

    // MARK: - Display helpers

    private var tailscaleStatusLabel: String {
        switch tailscale.status {
        case .up:       return "Tailscale: Up"
        case .degraded: return "Tailscale: Degraded"
        case .down:     return "Tailscale: Down"
        }
    }

    private var lastSyncLabel: String {
        guard let sync = health.lastSync else { return "Never synced" }
        return "Last sync: \(sync)"
    }
}
