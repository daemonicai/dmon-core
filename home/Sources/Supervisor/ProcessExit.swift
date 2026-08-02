import Dispatch
#if canImport(Darwin)
import Darwin
#endif

/// Awaits `pid`'s termination and reaps it.
///
/// Unlike `Process`, which reaps for you and hands back `terminationHandler`,
/// `posix_spawn` leaves reaping entirely to the caller: an unreaped exited
/// child becomes a zombie that nothing notices. This is the seam task 4.4's
/// crash detection is expected to build on — call it once per spawned
/// child's pid and restart when it resolves. `DispatchSource.makeProcessSource`
/// is the Darwin route that does not block a thread while waiting; the
/// actual reap happens via `waitpid` inside its event handler, once the
/// kernel has already told us the process exited.
///
/// Resolves exactly once per pid. Calling it twice for the same pid after
/// the first call has reaped it is a programmer error: the second `waitpid`
/// would fail `ECHILD`, since there is no longer a zombie left to collect.
public func awaitExit(of pid: pid_t) async -> ChildExitStatus {
    await withCheckedContinuation { (continuation: CheckedContinuation<ChildExitStatus, Never>) in
        let source = DispatchSource.makeProcessSource(identifier: pid, eventMask: .exit, queue: .global())
        source.setEventHandler {
            source.cancel()
            continuation.resume(returning: reap(pid))
        }
        source.resume()
    }
}

/// `sys/wait.h`'s `WIFEXITED` / `WEXITSTATUS` / `WIFSIGNALED` / `WTERMSIG` are
/// function-like macros, which the Clang importer does not expose to Swift
/// (confirmed directly: importing `Darwin` and calling any of them fails
/// "cannot find in scope" with a note that function-like macros are
/// unsupported) — so their bit layout is reproduced by hand here instead.
/// `waitpid` is called without `WUNTRACED`, so `status` only ever encodes
/// "exited" or "signalled", never "stopped".
private func reap(_ pid: pid_t) -> ChildExitStatus {
    var status: Int32 = 0
    waitpid(pid, &status, 0)
    let exitedNormally = (status & 0x7f) == 0
    if exitedNormally {
        return ChildExitStatus(pid: pid, exitCode: (status >> 8) & 0xff, terminatingSignal: nil)
    }
    return ChildExitStatus(pid: pid, exitCode: nil, terminatingSignal: status & 0x7f)
}
