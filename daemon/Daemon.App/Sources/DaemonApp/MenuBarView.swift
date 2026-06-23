import SwiftUI

struct MenuBarView: View {

    @EnvironmentObject var gateway: GatewayManager
    @EnvironmentObject var tailscale: TailscaleMonitor
    @EnvironmentObject var health: DcalHealthMonitor
    @EnvironmentObject var healthRegistry: HealthRegistry
    @EnvironmentObject var controller: DaemonController

    @Environment(\.openWindow) private var openWindow

    var body: some View {
        // One status row per registered component, in stable registry order.
        ForEach(Array(healthRegistry.components.enumerated()), id: \.offset) { _, component in
            HStack(spacing: 6) {
                Image(systemName: statusSymbol(component.status))
                    .foregroundStyle(statusColor(component.status))
                Text(component.name)
                if let detail = component.detail {
                    Text(detail)
                        .foregroundStyle(.secondary)
                        .font(.caption)
                }
            }
        }

        Divider()

        // Start/Stop Gateway
        Button(gateway.isRunning ? "Stop Gateway" : "Start Gateway") {
            if gateway.isRunning {
                gateway.stop()
            } else {
                gateway.start()
            }
        }

        // Bring Tailscale up (best-effort; outcome reflected via re-poll, not direct row mutation)
        Button("Bring Tailscale up") {
            Task { await tailscale.bringUp() }
        }

        // Sync Calendar Now
        Button("Sync Calendar Now") {
            Task {
                await health.syncNow()
            }
        }

        // Open Settings — sets the shared selectedSection on the controller then
        // opens/focuses the dashboard window. Uses the same code path as ⌘,.
        Button("Open Settings…") {
            controller.selectedSection = .settings
            openWindow(id: "dashboard")
            NSApp.activate(ignoringOtherApps: true)
        }

        // Quit
        Button("Quit") {
            NSApplication.shared.terminate(nil)
        }
    }
}
