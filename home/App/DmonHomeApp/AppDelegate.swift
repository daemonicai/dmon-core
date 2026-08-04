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
        super.init()
    }

    /// Bounds how long quitting waits for `hostRuntime.shutdownForTermination()`
    /// before giving up and replying anyway.
    ///
    /// This is a backstop, not the primary bound: `HostSupervisor.shutdown()`
    /// already bounds each child's own graceful-termination wait (default 5s)
    /// before escalating to `SIGKILL`, so the inventory's six children give a
    /// realistic worst case around 30s if every one of them ignored `SIGTERM`
    /// in sequence. 45s sits comfortably above that worst case — generous
    /// enough not to cut a normal, if slow, shutdown short, but still finite:
    /// a child (or a future bug) that wedges the wait entirely must not be
    /// able to hang quit forever.
    ///
    /// Deliberately **not** used to cancel `shutdownForTermination()`: the two
    /// `Task`s below race independently (neither cancels the other), so a
    /// slow shutdown keeps running toward its own sweep in the background
    /// even after the budget has already forced a reply — see
    /// `replyToTerminate`.
    private let terminationBudget: TimeInterval = 45

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
