import SwiftUI
import AppKit

// MARK: - App delegate (6.4 — orderly terminate-on-quit)

/// Terminates all supervised server processes and clears their PID files when the
/// app quits.  Marked `@MainActor` so that holding references to `@MainActor`-isolated
/// managers is safe without concurrency gymnastics, and because `applicationWillTerminate`
/// is always called on the main thread.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {

    var gateway: GatewayManager?
    var dcal: ServiceManager?
    var dmail: ServiceManager?

    nonisolated func applicationWillTerminate(_ notification: Notification) {
        MainActor.assumeIsolated {
            gateway?.stop()
            dcal?.stop()
            dmail?.stop()
        }
    }
}

@main
struct DaemonApp: App {

    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    @StateObject private var gateway = GatewayManager()
    @StateObject private var tailscale = TailscaleMonitor()
    @StateObject private var health = DcalHealthMonitor()
    @StateObject private var dcal = ServiceManager.makeDcal()
    @StateObject private var dmail = ServiceManager.makeDmail()
    @StateObject private var healthRegistry = HealthRegistry()

    // MARK: - Endpoint health monitors (tasks 5.1–5.3)

    @StateObject private var mailHealth = DmailHealthMonitor()

    @StateObject private var e2bProbe = EndpointHealthProbe(
        name: "E2B Endpoint",
        url: URL(string: ProcessInfo.processInfo.environment["DMON_E2B_URL"] ?? "http://localhost:11434")
    )

    @StateObject private var reasonerProbe = EndpointHealthProbe(
        name: "Reasoner Endpoint",
        url: URL(string: ProcessInfo.processInfo.environment["DMON_REASONER_URL"] ?? "http://localhost:8080/v1")
    )

    // Egress (Gemini) base URL is fixed — no env var override (Daemon.cs has no endpoint
    // override for GeminiChatClient either).
    @StateObject private var egressProbe = EndpointHealthProbe(
        name: "Egress Endpoint",
        url: URL(string: "https://generativelanguage.googleapis.com")
    )

    @State private var isInserted = true

    // MARK: - Icon color from aggregate rollup

    private func color(for rollup: RollupColor) -> Color {
        switch rollup {
        case .green: return .green
        case .amber: return .orange
        case .red:   return .red
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
                .environmentObject(healthRegistry)
                .task {
                    gateway.start()
                    tailscale.start()
                    health.start()
                    // Start supervised servers. If no executable resolves (no env var
                    // and no path override), start() is a no-op (isRunning stays false).
                    // This is the designed "not configured" terminal state (design.md D2).
                    dcal.start()
                    dmail.start()

                    // Wire the delegate so applicationWillTerminate calls stop() on all
                    // three managers (terminates children + clears PID files).
                    appDelegate.gateway = gateway
                    appDelegate.dcal = dcal
                    appDelegate.dmail = dmail

                    // Endpoint health monitors (tasks 5.1–5.3).
                    mailHealth.start()
                    e2bProbe.start()
                    reasonerProbe.start()
                    egressProbe.start()

                    // Wire the health registry.
                    // Stable display order: Gateway(0) Dcal(1) Dmail(2) Tailscale(3) Calendar Sync(4)
                    //                       Mail(5) E2B Endpoint(6) Reasoner Endpoint(7) Egress Endpoint(8).
                    healthRegistry.register(publisher: gateway.$componentHealth, order: 0)
                    healthRegistry.register(publisher: dcal.$componentHealth, order: 1)
                    healthRegistry.register(publisher: dmail.$componentHealth, order: 2)
                    healthRegistry.register(publisher: tailscale.$componentHealth, order: 3)
                    healthRegistry.register(publisher: health.$componentHealth, order: 4)
                    healthRegistry.register(publisher: mailHealth.$componentHealth, order: 5)
                    healthRegistry.register(publisher: e2bProbe.$componentHealth, order: 6)
                    healthRegistry.register(publisher: reasonerProbe.$componentHealth, order: 7)
                    healthRegistry.register(publisher: egressProbe.$componentHealth, order: 8)
                    // The Gateway's special icon role (stopped → red) is driven by a
                    // dedicated flag, NOT by forcing its ComponentHealth to `down`.
                    healthRegistry.observeGatewayStopped(gateway.$isRunning.map { !$0 })
                }
        } label: {
            Image(systemName: "brain")
                .foregroundStyle(color(for: healthRegistry.rollupColor))
        }

        Settings {
            // Pass the same GatewayManager instance used by the menu bar;
            // Settings scenes do NOT inherit MenuBarExtra's environmentObject chain.
            SettingsView()
                .environmentObject(gateway)
        }
    }
}
