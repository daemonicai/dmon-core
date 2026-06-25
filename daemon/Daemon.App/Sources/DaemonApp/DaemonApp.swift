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

// MARK: - Menu-bar preference constants

/// Single source of truth for the show-menu-bar-icon preference key and default.
///
/// Used by both the `@AppStorage` binding in `DaemonApp` and by `MenuBarPreferenceTests`
/// so neither side hard-codes a magic string or duplicates the default value.
enum MenuBarPreference {
    static let key = "showMenuBarIcon"
    static let defaultValue = false
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

    // Persisted default-off tray-icon preference (D4, task 4.2).
    // @AppStorage reads/writes UserDefaults.standard keyed by MenuBarPreference.key.
    @AppStorage(MenuBarPreference.key) private var showTrayIcon = MenuBarPreference.defaultValue

    var body: some Scene {
        // Primary scene: the dashboard window. Stable id so openWindow(id:) can
        // reopen/focus it if the user closed it (close ≠ quit).
        WindowGroup(id: "dashboard") {
            DashboardView()
                .environmentObject(controller)
        }
        .commands {
            // Rebind ⌘, to select the Settings section in the dashboard window.
            // The default `Settings` scene has been removed (D5); this replaces its
            // ⌘, binding so the shortcut selects in-window rather than opening a
            // separate window. Sets controller.selectedSection — the same shared
            // published property that the menu-bar button and sidebar binding use.
            CommandGroup(replacing: .appSettings) {
                Button("Settings…") {
                    controller.selectedSection = .settings
                    NSApp.activate(ignoringOtherApps: true)
                }
                .keyboardShortcut(",", modifiers: .command)
            }
        }

        // Optional tray icon. Off by default; persisted via @AppStorage (D4, task 4.2).
        MenuBarExtra(isInserted: $showTrayIcon) {
            MenuBarView()
                .environmentObject(controller.network)
                .environmentObject(controller.tailscale)
                .environmentObject(controller.calendarSync)
                .environmentObject(controller.dcal)
                .environmentObject(controller.dmail)
                .environmentObject(controller.healthRegistry)
                .environmentObject(controller)
        } label: {
            Image(systemName: "brain")
                .foregroundStyle(rollupColor(controller.healthRegistry.rollupColor))
        }
    }
}
