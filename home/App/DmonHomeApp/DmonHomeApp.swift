import SwiftUI

@main
struct DmonHomeApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        WindowGroup {
            ContentView(observers: appDelegate.observers, coordinator: appDelegate.coordinator)
        }
    }
}
