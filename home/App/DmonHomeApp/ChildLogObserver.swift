import Observation
import Supervisor

/// A thin mirror of `HostRuntime.logStore`'s live feed for SwiftUI to
/// observe.
///
/// Deliberately does no merging, filtering, or interpretation of its own —
/// `ChildLogStore` already decides retention, capacity, and drop-counting,
/// so all of that stays reachable by `swift test`. This type only consumes
/// the stream and stores the latest value.
///
/// **Construct exactly one of these per `HostRuntime`, and never let a view
/// construct its own.** Same reasoning as `ChildStatusObserver`, in full in
/// its own doc comment: `ContentView` gets a fresh identity every time its
/// window is closed and reopened from the Dock, so a view-constructed
/// observer would subscribe a new, never-cancelled `observationTask` to
/// `HostRuntime` on every reopen. `AppDelegate` — singular for the process —
/// constructs the one `ChildLogObserver` alongside `HostRuntime` and hands
/// it to `ContentView` by reference; `ContentView` never calls this
/// initializer itself.
@MainActor
@Observable
final class ChildLogObserver {
    private(set) var buffers: ChildLogStore.Snapshot = [:]
    private var observationTask: Task<Void, Never>?

    init(hostRuntime: HostRuntime) {
        observationTask = nil
        observationTask = Task { [weak self] in
            for await snapshot in await hostRuntime.logStore.updates() {
                self?.buffers = snapshot
            }
        }
    }
}
