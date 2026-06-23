import SwiftUI
import AppKit

// MARK: - App delegate

/// Owns the `DaemonController` and drives the application lifecycle.
///
/// `applicationDidFinishLaunching` calls `controller.bootstrap()` once, window-independently,
/// so supervision starts even on a headless login-item launch.
///
/// `applicationShouldTerminateAfterLastWindowClosed` returns `false` so closing the
/// dashboard window does not quit the app (close ≠ quit, D3).
///
/// `applicationWillTerminate` delegates to `controller.shutdown()` for orderly teardown.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {

    /// Single controller instance.  Created here so the `DaemonApp` struct can seed its
    /// `@StateObject` from this reference (see `DaemonApp.init()`), ensuring one instance
    /// is shared between the delegate lifecycle callbacks and the SwiftUI scene chain.
    let controller = DaemonController()

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Keep the app in the Dock (regular policy); close ≠ quit.
        NSApp.setActivationPolicy(.regular)
        // Bootstrap supervision once, window-independently.
        controller.bootstrap()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    nonisolated func applicationWillTerminate(_ notification: Notification) {
        MainActor.assumeIsolated {
            controller.shutdown()
        }
    }
}

@main
struct DaemonApp: App {

    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    // Seed the StateObject from the delegate's controller so the same instance is
    // observed by both the delegate lifecycle callbacks and the SwiftUI scene chain.
    // bootstrap() is NOT called here — only from applicationDidFinishLaunching.
    @StateObject private var controller: DaemonController

    init() {
        // @NSApplicationDelegateAdaptor stores its delegate as a @StateObject, so the
        // AppDelegate instance already exists by the time App.init() runs (SwiftUI
        // initialises all @StateObject property wrappers before calling init).
        // Capture the delegate reference first to avoid the escaping-autoclosure-captures-
        // mutating-self error that arises from passing _appDelegate.wrappedValue directly
        // into the StateObject(wrappedValue:) autoclosure.
        let existingDelegate = _appDelegate.wrappedValue
        _controller = StateObject(wrappedValue: existingDelegate.controller)
    }

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
                .environmentObject(controller.gateway)
                .environmentObject(controller.tailscale)
                .environmentObject(controller.calendarSync)
                .environmentObject(controller.dcal)
                .environmentObject(controller.dmail)
                .environmentObject(controller.healthRegistry)
        } label: {
            Image(systemName: "brain")
                .foregroundStyle(color(for: controller.healthRegistry.rollupColor))
        }

        Settings {
            // Settings scenes do NOT inherit MenuBarExtra's environmentObject chain.
            SettingsView()
                .environmentObject(controller.gateway)
        }
    }
}
