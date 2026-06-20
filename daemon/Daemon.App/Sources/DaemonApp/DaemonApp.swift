import SwiftUI

@main
struct DaemonApp: App {

    @StateObject private var gateway = GatewayManager()
    @StateObject private var tailscale = TailscaleMonitor()
    @StateObject private var health = DcalHealthMonitor()
    @StateObject private var dcal = ServiceManager.makeDcal()
    @StateObject private var dmail = ServiceManager.makeDmail()

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
                .environmentObject(dcal)
                .environmentObject(dmail)
                .task {
                    gateway.start()
                    tailscale.start()
                    health.start()
                    // Start supervised servers. If no executable resolves (no env var
                    // and no path override), start() is a no-op (isRunning stays false).
                    // This is the designed "not configured" terminal state (design.md D2).
                    //
                    // NOTE: Terminate-on-quit is NOT explicitly wired for the Gateway or
                    // these managers — there is no NSApplicationDelegateAdaptor calling
                    // stop(). Child processes launched via Process inherit the parent's
                    // process group and are killed by the OS when the app exits, but PID
                    // files are not cleaned up. This is a pre-existing gap shared by the
                    // Gateway; see flag in hand-off to orchestrator.
                    dcal.start()
                    dmail.start()
                }
        } label: {
            Image(systemName: "brain")
                .foregroundStyle(iconColor)
        }

        Settings {
            // Pass the same GatewayManager instance used by the menu bar;
            // Settings scenes do NOT inherit MenuBarExtra's environmentObject chain.
            SettingsView()
                .environmentObject(gateway)
        }
    }
}
