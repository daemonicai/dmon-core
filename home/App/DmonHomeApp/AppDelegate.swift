import AppKit
import Supervisor
import os

/// Owns the app's `HostRuntime` for its whole lifetime, and is the one place
/// that can bound app-exit correctly.
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

    /// Constructed exactly once here, alongside `hostRuntime`, and handed to
    /// `ContentView` by reference — see `ChildStatusObserver`'s own doc
    /// comment for why that single-construction guarantee matters.
    let statusObserver: ChildStatusObserver

    private let logger = Logger(subsystem: "ai.daemonic.dmon-home", category: "AppDelegate")

    override init() {
        let hostRuntime = HostRuntime()
        self.hostRuntime = hostRuntime
        self.statusObserver = ChildStatusObserver(hostRuntime: hostRuntime)
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
    /// **That non-cancellation is a safety precondition, not merely a design
    /// preference — do not change it without reading this.** `HostSupervisor`
    /// used to carry a `terminateSpawnedProcessGroups()` sweep as a backstop
    /// for a shutdown cut short mid-flight. That backstop was removed once
    /// `shutdownChild` began killing each child's process group on its
    /// *graceful* path too, on the strength of exactly one fact: nothing
    /// cancels this task, so `shutdown()` always runs to completion and the
    /// group kill always fires. Make `shutdownForTermination()` cancellable
    /// here — a `SIGTERM` handler with its own cancel-and-timeout, a
    /// different `.terminateLater` strategy — and a child's process group can
    /// once again survive host exit, **with no compiler or test signal**,
    /// because the mechanism that used to catch that class of regression is
    /// the one this precondition justified deleting.
    private let terminationBudget: TimeInterval

    private var didReplyToTerminate = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        Task { await hostRuntime.start() }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        Task { @MainActor in
            let refused = await hostRuntime.shutdownForTermination()
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
