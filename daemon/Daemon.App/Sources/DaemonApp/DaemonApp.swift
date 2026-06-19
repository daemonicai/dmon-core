import SwiftUI

@main
struct DaemonApp: App {
    var body: some Scene {
        MenuBarExtra("Daemon", systemImage: "brain") {
            // Placeholder menu content; real MenuBarView arrives in §9.
            Text("Daemon")
            Divider()
            Button("Quit") {
                NSApplication.shared.terminate(nil)
            }
        }

        Settings {
            // Placeholder settings; real SettingsView arrives in §10.
            Text("Settings")
                .padding()
        }
    }
}
