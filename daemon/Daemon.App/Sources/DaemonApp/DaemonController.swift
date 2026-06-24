import AppKit
import Combine
import Foundation

// MARK: - DaemonController

/// Lifecycle owner for all supervised managers and health subscriptions.
///
/// Owns the `GatewayManager`, two `ServiceManager`s, the four monitors / probes,
/// and the `HealthRegistry`.  A single `@StateObject` on the `DaemonApp` struct
/// replaces the previous eleven scattered `@StateObject`s.
///
/// `bootstrap()` is idempotent — a private `hasBootstrapped` flag makes every call
/// after the first a no-op.  It is invoked exactly once from
/// `AppDelegate.applicationDidFinishLaunching` (window-independent, before any
/// scene is shown).
///
/// `shutdown()` reproduces the orderly teardown that previously lived in
/// `AppDelegate.applicationWillTerminate`.
@MainActor
final class DaemonController: ObservableObject {

    // MARK: - Owned managers (internal so tests can inspect)

    let gateway = GatewayManager()
    let dcal    = ServiceManager.makeDcal()
    let dmail   = ServiceManager.makeDmail()

    // Monitors
    let tailscale     = TailscaleMonitor()
    let calendarSync  = DcalHealthMonitor()
    let mailMonitor   = DmailHealthMonitor()

    // Endpoint probes — URLs resolved once at construction (env is stable).
    let e2bProbe = EndpointHealthProbe(
        name: "E2B Endpoint",
        url: URL(string: ProcessInfo.processInfo.environment["DMON_E2B_URL"] ?? "http://localhost:11434")
    )
    let reasonerProbe = EndpointHealthProbe(
        name: "Reasoner Endpoint",
        url: URL(string: ProcessInfo.processInfo.environment["DMON_REASONER_URL"] ?? "http://localhost:8080/v1")
    )
    // Egress (Gemini) base URL is fixed — no env var override.
    let egressProbe = EndpointHealthProbe(
        name: "Egress Endpoint",
        url: URL(string: "https://generativelanguage.googleapis.com")
    )

    let healthRegistry = HealthRegistry()

    // MARK: - Dashboard selection

    /// Current sidebar selection in the dashboard window.
    /// Shared by `DashboardView`, the App-level `.commands` handler, and `MenuBarView`
    /// so every entry point (sidebar click, ⌘,, menu-bar button) drives the same state.
    @Published var selectedSection: DashboardSection = .status

    // MARK: - Idempotence guard

    /// True once `bootstrap()` has run to completion.  Subsequent calls are no-ops.
    private(set) var hasBootstrapped = false

    private var cancellables: Set<AnyCancellable> = []

    // MARK: - Bootstrap

    /// Start all managers, register the nine health publishers (stable orders 0–8),
    /// wire `observeGatewayStopped`, and start the monitors and probes.
    ///
    /// Guarded by `hasBootstrapped` — calling more than once is a safe no-op.
    /// Called from `AppDelegate.applicationDidFinishLaunching` (window-independent).
    func bootstrap() {
        guard !hasBootstrapped else { return }
        hasBootstrapped = true

        // Start supervised servers.  start() is adoption-guarded in ServerProcessManager
        // and poll-task-guarded in all monitors, so re-entry within a single call is safe.
        // If no executable resolves (no env var / no path override), start() is a no-op.
        gateway.start()
        tailscale.start()
        calendarSync.start()
        dcal.start()
        dmail.start()

        // Endpoint health monitors.
        mailMonitor.start()
        e2bProbe.start()
        reasonerProbe.start()
        egressProbe.start()

        // Wire the health registry.
        // Stable display order: Gateway(0) Dcal(1) Dmail(2) Tailscale(3) Calendar Sync(4)
        //                       Mail(5) E2B Endpoint(6) Reasoner Endpoint(7) Egress Endpoint(8).
        healthRegistry.register(publisher: gateway.$componentHealth, order: 0)
        healthRegistry.register(publisher: dcal.$componentHealth,    order: 1)
        healthRegistry.register(publisher: dmail.$componentHealth,   order: 2)
        healthRegistry.register(publisher: tailscale.$componentHealth,    order: 3)
        healthRegistry.register(publisher: calendarSync.$componentHealth, order: 4)
        healthRegistry.register(publisher: mailMonitor.$componentHealth,  order: 5)
        healthRegistry.register(publisher: e2bProbe.$componentHealth,     order: 6)
        healthRegistry.register(publisher: reasonerProbe.$componentHealth, order: 7)
        healthRegistry.register(publisher: egressProbe.$componentHealth,  order: 8)

        // The Gateway's special icon role (stopped → red) is driven by a dedicated flag,
        // NOT by forcing its ComponentHealth to `down`.
        healthRegistry.observeGatewayStopped(gateway.$isRunning.map { !$0 })

        // Drive the Dock icon tint from rollupColor (D6 / task 4.1).
        // Subscribed here — once, window-independently — so the tint updates even when
        // the dashboard window is closed (headless login-item launch case).
        healthRegistry.$rollupColor
            .receive(on: RunLoop.main)
            .sink { color in
                NSApp.applicationIconImage = tintedAppIcon(rollupNSColor(color))
            }
            .store(in: &cancellables)
    }

    // MARK: - Shutdown

    /// Orderly teardown — stops all supervised processes and clears their PID files.
    /// Called from `AppDelegate.applicationWillTerminate`.
    func shutdown() {
        gateway.stop()
        dcal.stop()
        dmail.stop()
    }
}
