import SwiftUI

@main
struct DaemonApp: App {

    @StateObject private var gateway = GatewayManager()
    @StateObject private var tailscale = TailscaleMonitor()
    @StateObject private var health = DcalHealthMonitor()

    @State private var isInserted = true

    // MARK: - Combined-status icon color (authoritative decision table)
    // Gateway stopped → red (takes precedence over Tailscale)
    // Gateway running + Tailscale up → green
    // Gateway running + Tailscale degraded or down → amber
    private var iconColor: Color {
        guard gateway.isRunning else { return .red }
        switch tailscale.status {
        case .up:               return .green
        case .degraded, .down:  return .orange
        }
    }

    var body: some Scene {
        MenuBarExtra(isInserted: $isInserted) {
            MenuBarView()
                .environmentObject(gateway)
                .environmentObject(tailscale)
                .environmentObject(health)
                .task {
                    gateway.start()
                    tailscale.start()
                    health.start()
                }
        } label: {
            Image(systemName: "brain")
                .foregroundStyle(iconColor)
        }

        Settings {
            // Placeholder settings; real SettingsView arrives in §10.
            Text("Settings")
                .padding()
        }
    }
}
