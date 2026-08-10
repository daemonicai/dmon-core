import GatewayClient
import Observation
import Supervisor

/// Every `@Observable` mirror the app target constructs, and the one file permitted to
/// construct any of them.
///
/// Each mirror below does no merging, filtering, or interpretation of its own — the actor it
/// wraps (`HostRuntime`'s two stores, or `SessionCoordinator`) already does all of that, so
/// that logic stays reachable by `swift test`. A mirror only consumes its stream and stores
/// the latest value; deciding what a status or connection state means is never its job.
///
/// **Construction is a compiler-enforced fact here, not a documented convention.** Each
/// mirror's `init` below is `fileprivate` — callable only from code in this file — so a view
/// anywhere else in the app target cannot construct its own; the attempt is a build error, not
/// a runtime leak with no signal. This exists because closing this app's window and reopening
/// it from the Dock is ordinary `WindowGroup` behaviour (`AppDelegate` does not override
/// `applicationShouldTerminateAfterLastWindowClosed`), and constructs a fresh `ContentView`
/// with a new identity on every reopen. A view-constructed mirror would run its `init` again on
/// every such reopen — `@State(initialValue:)` only *uses* its first-constructed value for a
/// given view identity, it does not stop the initializer expression itself from running — so
/// each discarded instance would already have subscribed via `observationTask`, leaking one
/// more subscriber into the actor underneath (`HostRuntime.statusSubscribers`, its `logStore`,
/// or `SessionCoordinator.subscribers`) permanently, since that `Task` is never cancelled (see
/// below). The app target has no test bundle, so that leak would carry no compiler or test
/// signal at all — only `fileprivate` construction rules it out by construction.
///
/// `AppObservers`, below, is what closes the leak in practice: it constructs exactly one of
/// each mirror, alongside `AppDelegate`'s `HostRuntime` and `SessionCoordinator`, and hands
/// them down by reference. `AppDelegate` — itself guaranteed singular for the process by
/// `NSApplicationDelegateAdaptor` — is the only caller. No number of window close/reopen
/// cycles can construct a second instance of any of the three, because nothing outside this
/// file is able to call the initializer that would.
///
/// No `deinit`-driven cancellation for any of the three, for the same reason none of them ever
/// had one: a `deinit` on a `@MainActor` class is itself `nonisolated` under Swift 6's strict
/// concurrency checking, so it cannot touch an actor-isolated stored property like
/// `observationTask` without an `isolated deinit` (not available in this toolchain). With
/// construction bounded to exactly once per process, there is nothing for a `deinit` to cancel
/// that outliving the process wouldn't already have ended anyway.
@MainActor
@Observable
final class ChildStatusObserver {
    private(set) var statuses: [ChildStatus] = []
    private var observationTask: Task<Void, Never>?

    fileprivate init(hostRuntime: HostRuntime) {
        observationTask = nil
        observationTask = Task { [weak self] in
            for await statuses in await hostRuntime.statusUpdates() {
                self?.statuses = statuses
            }
        }
    }
}

/// A thin mirror of `HostRuntime.logStore`'s live feed for SwiftUI to observe. See
/// `ChildStatusObserver`'s doc comment above for why its `init` is `fileprivate` and what that
/// guarantees.
@MainActor
@Observable
final class ChildLogObserver {
    private(set) var buffers: ChildLogStore.Snapshot = [:]
    private var observationTask: Task<Void, Never>?

    fileprivate init(hostRuntime: HostRuntime) {
        observationTask = nil
        observationTask = Task { [weak self] in
            for await snapshot in await hostRuntime.logStore.updates() {
                self?.buffers = snapshot
            }
        }
    }
}

/// A thin mirror of `SessionCoordinator.updates()` for SwiftUI to observe. See
/// `ChildStatusObserver`'s doc comment above for why its `init` is `fileprivate` and what that
/// guarantees. `snapshot` is `nil` only until the stream's first value arrives — `updates()`
/// yields the coordinator's current snapshot immediately to every new subscriber, so that
/// window is brief, not a state this observer's own subscribers need to plan around.
@MainActor
@Observable
final class SessionObserver {
    private(set) var snapshot: SessionSnapshot?
    private var observationTask: Task<Void, Never>?

    fileprivate init(coordinator: SessionCoordinator) {
        observationTask = nil
        observationTask = Task { [weak self] in
            for await snapshot in await coordinator.updates() {
                self?.snapshot = snapshot
            }
        }
    }
}

/// Owns the app's one instance of each mirror above, constructed alongside `AppDelegate`'s
/// `HostRuntime` and `SessionCoordinator`. `AppDelegate` holds exactly one `AppObservers`;
/// `ContentView`/`DmonHomeApp` take what they need from it, by reference, never constructing a
/// mirror of their own — see each mirror's own doc comment for why that matters.
@MainActor
final class AppObservers {
    let status: ChildStatusObserver
    let log: ChildLogObserver
    let session: SessionObserver

    init(hostRuntime: HostRuntime, coordinator: SessionCoordinator) {
        self.status = ChildStatusObserver(hostRuntime: hostRuntime)
        self.log = ChildLogObserver(hostRuntime: hostRuntime)
        self.session = SessionObserver(coordinator: coordinator)
    }
}
