import SwiftUI

@main
struct DmonHomeApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        WindowGroup {
            ContentView(statusObserver: appDelegate.statusObserver, logObserver: appDelegate.logObserver)
        }
    }
}
