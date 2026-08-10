import Dispatch
import os
#if canImport(Darwin)
import Darwin
#endif

/// What `awaitExit(of:)` observed.
public enum AwaitExitOutcome: Sendable {
    /// The process exited and was reaped.
    case exited(ChildExitStatus)

    /// The calling `Task` was cancelled before the process was observed to
    /// exit. The pid was **not** reaped — ownership of it is fully
    /// relinquished, so a caller that goes on to force the process to exit
    /// (e.g. `SIGKILL`) is free to make a fresh `awaitExit(of:)` call.
    case cancelled

    /// `waitpid` itself failed (typically `ECHILD`) rather than reporting an
    /// exit — most likely because `pid` had already been reaped by an
    /// earlier call. Distinct from `.cancelled`: nothing here was abandoned,
    /// the attempt to reap actively failed. The `errno` value is preserved
    /// rather than interpreted, mirroring `SpawnError.posixSpawnFailed`.
    case reapFailed(errno: Int32)
}

/// Awaits `pid`'s termination and reaps it — or, if the calling `Task` is
/// cancelled first, gives up watching without reaping.
///
/// Unlike `Process`, which reaps for you and hands back `terminationHandler`,
/// `posix_spawn` leaves reaping entirely to the caller: an unreaped exited
/// child becomes a zombie that nothing notices. This is the seam task 4.4's
/// crash detection is built on — call it once per spawned child's pid and
/// restart when it resolves. `DispatchSource.makeProcessSource` is the Darwin
/// route that does not block a thread while waiting; the actual reap happens
/// via `waitpid` inside its event handler, once the kernel has already told
/// us the process exited.
///
/// **Cancellation is honoured**, via `withTaskCancellationHandler` — this is
/// what makes a bounded wait around this function (or around a `Task` whose
/// body calls it) genuinely bounded, rather than "bounded" only in the sense
/// that a losing race returns a value while the underlying wait keeps running
/// forever regardless. A caller that needs a wait it can actually give up on
/// (rather than merely stop caring about) must propagate its own
/// cancellation into this call — see `HostSupervisor.shutdownChild`, which
/// wraps `await task.value` in its own `withTaskCancellationHandler` for
/// exactly this reason: cancelling a task that is merely *awaiting another
/// task's `.value`* does not, on its own, reach into that other task.
///
/// A caller for whom this reap is the last chance to avoid an unreaped
/// zombie (there is no fourth attempt) must shield the call from its own
/// task's cancellation instead — see `HostSupervisor.shutdownChild`'s
/// post-`SIGKILL` reap, which runs this inside a fresh, uncancelled `Task`
/// for exactly that reason.
///
/// **Reaping on cancellation is deliberately not attempted.** The process may
/// not have exited yet — resuming with a fabricated `ChildExitStatus` would
/// be dishonest, and reaping is only safe once the kernel has actually
/// reported the exit.
///
/// Resolves exactly once per pid **while genuinely waited on**. Calling it
/// twice concurrently for the same pid — two live, uncancelled calls racing
/// each other — is a programmer error: only one `waitpid` can succeed, and
/// this function does not detect or guard against that misuse (single
/// ownership is enforced structurally by `HostSupervisor`, which never
/// starts a second live wait for a pid it is already waiting on). Calling it
/// again *after* a prior call has already reaped the pid is reported as
/// `.reapFailed` rather than fabricated as a successful exit — see `reap(_:)`.
public func awaitExit(of pid: pid_t) async -> AwaitExitOutcome {
    let box = ExitWaitBox()
    return await withTaskCancellationHandler(
        operation: {
            await withCheckedContinuation { (continuation: CheckedContinuation<AwaitExitOutcome, Never>) in
                box.start(pid: pid, continuation: continuation)
            }
        },
        onCancel: {
            box.cancel()
        }
    )
}

/// Arbitrates between the two ways `awaitExit`'s wait can end — the process
/// actually exiting (observed on `DispatchSource`'s queue) and the awaiting
/// `Task` being cancelled (observed on whatever thread requests cancellation)
/// — so exactly one of them resumes the continuation, never both, regardless
/// of which happens first. `OSAllocatedUnfairLock` rather than
/// `@unchecked Sendable`/`nonisolated(unsafe)` (ADR/design D14 reserves those
/// for the audio ring buffer only): it is a real, checked synchronisation
/// primitive, not a suppressed diagnostic.
private final class ExitWaitBox: Sendable {
    private struct State {
        var source: DispatchSourceProcess?
        var continuation: CheckedContinuation<AwaitExitOutcome, Never>?
        var settled = false
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    func start(pid: pid_t, continuation: CheckedContinuation<AwaitExitOutcome, Never>) {
        let source = DispatchSource.makeProcessSource(identifier: pid, eventMask: .exit, queue: .global())
        source.setEventHandler { [weak self] in
            self?.resumeFromExit(pid: pid)
        }

        let alreadySettled = state.withLock { s -> Bool in
            if s.settled { return true }
            s.source = source
            s.continuation = continuation
            return false
        }

        guard !alreadySettled else {
            // `cancel()` ran before `start()` could install the source —
            // possible in principle (cancellation can be requested at any
            // point), not observed in practice. Resume without ever having
            // watched anything, rather than leaking `continuation`.
            continuation.resume(returning: .cancelled)
            return
        }
        source.resume()
    }

    private func resumeFromExit(pid: pid_t) {
        let continuationToResume = state.withLock { s -> CheckedContinuation<AwaitExitOutcome, Never>? in
            guard !s.settled else { return nil }
            s.settled = true
            s.source?.cancel()
            s.source = nil
            let c = s.continuation
            s.continuation = nil
            return c
        }
        guard let continuationToResume else { return }
        continuationToResume.resume(returning: reap(pid))
    }

    func cancel() {
        let continuationToResume = state.withLock { s -> CheckedContinuation<AwaitExitOutcome, Never>? in
            guard !s.settled else { return nil }
            s.settled = true
            s.source?.cancel()
            s.source = nil
            let c = s.continuation
            s.continuation = nil
            return c
        }
        continuationToResume?.resume(returning: .cancelled)
    }
}

/// `sys/wait.h`'s `WIFEXITED` / `WEXITSTATUS` / `WIFSIGNALED` / `WTERMSIG` are
/// function-like macros, which the Clang importer does not expose to Swift
/// (confirmed directly: importing `Darwin` and calling any of them fails
/// "cannot find in scope" with a note that function-like macros are
/// unsupported) — so their bit layout is reproduced by hand here instead.
/// `waitpid` is called without `WUNTRACED`, so `status` only ever encodes
/// "exited" or "signalled", never "stopped".
///
/// `waitpid`'s own return is checked rather than trusted blind: for a pid
/// this process has already reaped (or never owned), `waitpid` fails —
/// typically `ECHILD` — and leaves `status` at its initialised `0`. Reading
/// that unconditionally, as an earlier version of this function did, decodes
/// to "exited normally, code 0": a fabricated success for a call that
/// observed nothing at all. `.reapFailed` reports the failure honestly
/// instead.
private func reap(_ pid: pid_t) -> AwaitExitOutcome {
    var status: Int32 = 0
    let result = waitpid(pid, &status, 0)
    guard result == pid else {
        return .reapFailed(errno: errno)
    }
    let exitedNormally = (status & 0x7f) == 0
    if exitedNormally {
        return .exited(ChildExitStatus(pid: pid, exitCode: (status >> 8) & 0xff, terminatingSignal: nil))
    }
    return .exited(ChildExitStatus(pid: pid, exitCode: nil, terminatingSignal: status & 0x7f))
}
