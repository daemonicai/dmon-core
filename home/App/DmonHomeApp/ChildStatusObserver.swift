import Observation
import Supervisor

/// A thin mirror of `HostRuntime.statusUpdates()` for SwiftUI to observe.
///
/// Deliberately does no merging, filtering, or interpretation of its own —
/// `HostRuntime` already does all of that, so it stays reachable by `swift
/// test`. This type only consumes the stream and stores the latest value.
///
/// **Construct exactly one of these per `HostRuntime`, and never let a view
/// construct its own.** An earlier version of this type was built by
/// `ContentView.init` via `@State(initialValue:)`; `@State` only *uses* its
/// first-constructed value for a given view identity, but the initializer
/// expression itself still runs unconditionally on every `ContentView.init`
/// call — and closing this app's window and reopening it from the Dock is
/// ordinary `WindowGroup` behaviour (`AppDelegate` does not override
/// `applicationShouldTerminateAfterLastWindowClosed`), which constructs a
/// fresh `ContentView` with a new identity on every reopen. Each discarded
/// instance's `init` had already started `observationTask`, subscribing to
/// `hostRuntime.statusUpdates()` — and since that `Task` is never cancelled
/// (see below), every reopen leaked one more subscriber into
/// `HostRuntime`'s `statusSubscribers`, permanently.
///
/// The fix is ownership, not cleanup: `AppDelegate` — itself guaranteed
/// singular for the process by `NSApplicationDelegateAdaptor` — constructs
/// the one `ChildStatusObserver` alongside its `HostRuntime` and hands it to
/// `ContentView` by reference; `ContentView` never calls this initializer
/// itself. No number of window close/reopen cycles can construct a second
/// one, so there is no repeat-construction leak surface left to guard
/// against — this is a property of who is allowed to call `init`, not an
/// assumption about how often SwiftUI happens to call it.
///
/// No `deinit`-driven cancellation either way: a `deinit` on a `@MainActor`
/// class is itself `nonisolated` under Swift 6's strict concurrency
/// checking, so it cannot touch an actor-isolated stored property like
/// `observationTask` without an `isolated deinit` (not available in this
/// toolchain). With construction now bounded to exactly once per process,
/// there is nothing for a `deinit` to cancel that outliving the process
/// wouldn't already have ended anyway.
@MainActor
@Observable
final class ChildStatusObserver {
    private(set) var statuses: [ChildStatus] = []
    private var observationTask: Task<Void, Never>?

    init(hostRuntime: HostRuntime) {
        observationTask = nil
        observationTask = Task { [weak self] in
            for await statuses in await hostRuntime.statusUpdates() {
                self?.statuses = statuses
            }
        }
    }
}
