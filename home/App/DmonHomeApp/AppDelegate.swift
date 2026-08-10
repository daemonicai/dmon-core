import AppKit
import DeviceKeys
import GatewayClient
import Power
import Supervisor
import os

/// Owns the app's `HostRuntime` and `SessionCoordinator` for its whole
/// lifetime, and is the one place that can bound app-exit correctly.
///
/// `applicationWillTerminate` is synchronous and cannot `await` an actor —
/// calling into `HostRuntime.shutdownForTermination()` from there would
/// either not compile or silently truncate shutdown by not waiting for it at
/// all. `applicationShouldTerminate` can defer the actual quit
/// (`.terminateLater`) while an async `Task` runs the real shutdown, then
/// replies once it is done.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let hostRuntime: HostRuntime

    /// Owns the create→attach handshake and its inbound stream (`GatewayClient`'s
    /// `SessionCoordinator`) over the real device-key-authenticated transport
    /// (`DeviceKeys`' `AuthenticatedTransportFactory`). Passes no `agent:` — the
    /// host picks its own default; naming one here would be a policy value this
    /// app target has no business inventing.
    let coordinator: SessionCoordinator

    /// Constructed exactly once here, alongside `hostRuntime` and
    /// `coordinator`, and handed to `ContentView` by reference — see
    /// `AppObservers`' own doc comment for why that single-construction
    /// guarantee matters and how it is enforced.
    let observers: AppObservers

    /// The activity-assertion policy (spec: "The host holds an activity
    /// assertion while the gateway is enabled"). Constructed alongside
    /// `hostRuntime`; told the gateway's enablement in
    /// `applicationDidFinishLaunching` and told `false` on the termination
    /// path in `applicationShouldTerminate`. This is wiring only — the
    /// policy values (the activity options, the reason string) live in
    /// `GatewayActivityPolicy`, not here.
    let activityPolicy: GatewayActivityPolicy

    private let logger = Logger(subsystem: "ai.daemonic.dmon-home", category: "AppDelegate")

    /// Watches for the network gateway child's first `.healthy` report and calls
    /// `coordinator.connect()` exactly once — see `connectOnceNetworkGatewayIsHealthy()`.
    /// Cancelled synchronously at the top of `applicationShouldTerminate`, before that method
    /// does anything else — see that method's own comment for why cancelling this alone is
    /// not sufficient by itself, and `connectOnceNetworkGatewayIsHealthy()`'s for the loop-body
    /// check this cancellation is paired with.
    private var connectTriggerTask: Task<Void, Never>?

    override init() {
        let hostRuntime = HostRuntime()
        self.hostRuntime = hostRuntime

        let endpoint = GatewayEndpoint(url: GatewayEndpoint.defaultURL)
        let fileReader = DevicesFileReader()
        let secretStore = KeychainDeviceKeySecretStore()
        let policy = DeviceAuthPolicy(fileReader: fileReader, secretStore: secretStore)
        let provisioner = DeviceKeyProvisioner(fileReader: fileReader, secretStore: secretStore)
        let transportFactory = AuthenticatedTransportFactory(endpoint: endpoint, policy: policy, provisioner: provisioner)
        let session = GatewaySession(makeTransport: transportFactory.makeTransport)
        let coordinator = SessionCoordinator(session: session)
        self.coordinator = coordinator

        self.observers = AppObservers(hostRuntime: hostRuntime, coordinator: coordinator)
        self.activityPolicy = GatewayActivityPolicy()
        self.terminationBudget = hostRuntime.worstCaseShutdownDuration + AppDelegate.terminationBudgetMargin
        super.init()
    }

    /// Headroom added on top of `HostRuntime.worstCaseShutdownDuration` to
    /// form `terminationBudget` — this backstop's own tolerance for a
    /// wedged wait/reply, not part of the worst-case shutdown math itself
    /// (that math already accounts for every enabled child's own graceful
    /// wait).
    private static let terminationBudgetMargin: TimeInterval = 15

    /// Bounds how long quitting waits for `hostRuntime.shutdownForTermination()`
    /// before giving up and replying anyway.
    ///
    /// This is a backstop, not the primary bound: `HostSupervisor.shutdown()`
    /// already bounds each enabled child's own graceful-termination wait
    /// before escalating to `SIGKILL`. Derived from `HostRuntime
    /// .worstCaseShutdownDuration` (plus `terminationBudgetMargin`) rather
    /// than a hand-computed constant, so enabling a seventh child or
    /// retuning `gracefulShutdownTimeout` changes this budget automatically
    /// instead of quietly invalidating a comment that used to derive it by
    /// hand — the two lived in different files with nothing keeping them in
    /// sync.
    ///
    /// Deliberately **not** used to cancel `shutdownForTermination()`: the two
    /// `Task`s below race independently (neither cancels the other), so a
    /// slow shutdown keeps running its own shutdown in the background
    /// even after the budget has already forced a reply — see
    /// `replyToTerminate`.
    ///
    /// `HostSupervisor` used to carry a `terminateSpawnedProcessGroups()`
    /// sweep as a backstop for a shutdown cut short mid-flight. It was
    /// removed once `shutdownChild` began killing each child's process group
    /// on its *graceful* branch too — so **cancelling this task does not
    /// re-open that hole.** Under cancellation `withTimeout`'s sleep throws
    /// and yields `nil` (escalation branch, group `SIGKILL`) or its operation
    /// returns `true` (graceful branch, group `SIGKILL`); both outcomes
    /// signal the group, so cancellation degrades to *kill everything fast*
    /// rather than *skip the kill*.
    ///
    /// **The residual exposure is narrower, and worth knowing before you
    /// restructure `HostSupervisor.shutdown()`:** its `for` loop is not a
    /// cancellation checkpoint, so it always walks every child. Add a
    /// `try Task.checkCancellation()` inside that loop — or rewrite it into a
    /// throwing form that bails — and children later in the reverse order are
    /// never signalled at all, **with no compiler or test signal**, because
    /// the deleted sweep iterated `states.values` rather than following the
    /// loop and was the only thing that would have caught it. The structural
    /// fix, if that day comes, is to make the walk uncancellable by
    /// construction rather than by this comment: see `## NEXT` in the change
    /// DEVLOG.
    private let terminationBudget: TimeInterval

    private var didReplyToTerminate = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        Task { await hostRuntime.start() }
        Task { await activityPolicy.apply(gatewayEnabled: hostRuntime.isGatewayEnabled) }
        connectTriggerTask = Task { [weak self] in
            await self?.connectOnceNetworkGatewayIsHealthy()
        }
    }

    /// Product Owner decision, 2026-08-09: auto-connect once, manual reconnect. Watches
    /// `hostRuntime.statusUpdates()` and calls `coordinator.connect()` the first time the
    /// network gateway child (`ChildInventory.networkGateway.id`, matched by id, never by
    /// display name) reports `.healthy`, then stops watching.
    ///
    /// Not at launch: a cold-spawned gateway takes up to `HostRuntime`'s health-check
    /// interval (default 30s) to answer, and attaching before that fails for a reason that
    /// tells nobody anything.
    ///
    /// This at-most-once guard is the only decision made here — a policy would not be
    /// permitted in the app target, but deciding *when* to call a package's own method once
    /// is. Everything about what `connect()` itself does — the handshake, its retry
    /// discipline (there is none; a second, later reconnect is a distinct, manual verb) —
    /// stays entirely `SessionCoordinator`'s.
    ///
    /// **This loop stays live for the whole span of a shutdown, not just before one starts —
    /// checked, not assumed.** `statusUpdates()` is fed by two forwarding tasks
    /// `HostRuntime.shutdownForTermination()` cancels only as its *last* step, after
    /// `supervisor.shutdown()` has already completed; until then a stale `.healthy` from just
    /// before the kill can still arrive here. The `Task.isCancelled` check below, paired with
    /// `applicationShouldTerminate` cancelling `connectTriggerTask` synchronously before it
    /// does anything else, is what keeps that from calling `coordinator.connect()` mid-shutdown
    /// — every value that arrives after cancellation is set now returns instead of connecting.
    /// That still leaves one gap neither app-target fix can reach: a call to `connect()` that
    /// was already past this check — genuinely suspended inside it — when `close()` runs
    /// concurrently on `SessionCoordinator`'s own actor. `SessionCoordinator.isClosed` is the
    /// structural fix for that gap; see its own doc comment.
    private func connectOnceNetworkGatewayIsHealthy() async {
        for await statuses in await hostRuntime.statusUpdates() {
            guard !Task.isCancelled else {
                return
            }
            guard let gateway = statuses.first(where: { $0.id == ChildInventory.networkGateway.id }) else {
                continue
            }
            guard gateway.health == .healthy else {
                continue
            }
            await coordinator.connect()
            return
        }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        // Cancelled synchronously, before either `Task` below is even created, so the flag is
        // already set for `connectOnceNetworkGatewayIsHealthy()`'s own `Task.isCancelled` check
        // no matter how the two happen to be scheduled relative to each other — see that
        // method's doc comment for why cancellation alone is not sufficient.
        connectTriggerTask?.cancel()
        Task { @MainActor in
            // Closed *before* `shutdownForTermination()`, not after — task 4.5's own
            // reverse-startup-order rule, applied consistently: `networkGateway` is
            // `startupOrder: 0`, the session is established after it and depends on it, so
            // reverse-dependency order tears the session down before that child, exactly as it
            // would for a sibling child. This is also the quieter sequence, not a slower one —
            // `WebSocketGatewayTransport.close()` bottoms out in a non-blocking
            // `task.cancel(with: .normalClosure, reason: nil)` that never waits on the peer, so
            // closing first costs nothing extra; it only changes whether `ndmon` sees an orderly
            // client disconnect while it is still alive, instead of its process being killed out
            // from under a connection this app never told it was ending.
            await coordinator.close()
            let refused = await hostRuntime.shutdownForTermination()
            await activityPolicy.apply(gatewayEnabled: false)
            replyToTerminate(refused: refused)
        }
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: UInt64(terminationBudget * 1_000_000_000))
            replyToTerminate(budgetExceeded: true)
        }
        return .terminateLater
    }

    /// Whichever of the two races above finishes first replies; the other is
    /// a no-op once `didReplyToTerminate` is set. This guarantees
    /// `NSApp.reply(toApplicationShouldTerminate:)` is always eventually
    /// called exactly once — even if `shutdownForTermination()` itself never
    /// returns, in which case the budget's own `Task` (an independent,
    /// uncancelled wait) still fires and replies on schedule.
    private func replyToTerminate(refused: [ChildID]? = nil, budgetExceeded: Bool = false) {
        guard !didReplyToTerminate else { return }
        didReplyToTerminate = true

        if budgetExceeded {
            logger.error("host shutdown exceeded its \(self.terminationBudget, format: .fixed(precision: 0))s termination budget; quitting anyway")
        }
        if let refused, !refused.isEmpty {
            let ids = refused.map(\.rawValue).joined(separator: ", ")
            logger.error("refused to signal process groups for: \(ids)")
        }

        NSApp.reply(toApplicationShouldTerminate: true)
    }
}
