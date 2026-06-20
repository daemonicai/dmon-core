import SwiftUI

struct MenuBarView: View {

    @EnvironmentObject var gateway: GatewayManager
    @EnvironmentObject var health: DcalHealthMonitor
    @EnvironmentObject var healthRegistry: HealthRegistry

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

    // MARK: - Status display helpers

    private func statusSymbol(_ status: HealthStatus) -> String {
        switch status {
        case .ok:       return "circle.fill"
        case .degraded: return "exclamationmark.circle.fill"
        case .down:     return "xmark.circle.fill"
        case .unknown:  return "questionmark.circle.fill"
        }
    }

    private func statusColor(_ status: HealthStatus) -> Color {
        switch status {
        case .ok:       return .green
        case .degraded: return .orange
        case .down:     return .red
        case .unknown:  return .gray
        }
    }
}
