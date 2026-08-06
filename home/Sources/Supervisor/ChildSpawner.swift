import Foundation
#if canImport(Darwin)
import Darwin
#endif

/// Launches a child in its own process group via `posix_spawn`, and kills
/// that group on demand.
///
/// `Foundation.Process` was considered and rejected: it hands back
/// stdout/stderr pipes for free (which section 5.1 needs), but it has no
/// hook between fork and exec, so it cannot place the child in a new process
/// group — `setpgid` called from the parent races the exec (and fails
/// `EACCES` once the exec has already happened), and macOS ships no
/// `setsid(1)` to shell out to instead. `posix_spawn`'s
/// `POSIX_SPAWN_SETPGROUP` attribute, with pgroup `0` (meaning "a new group
/// whose pgid equals the child's own pid"), gets both properties — pipes and
/// an isolated group — from a single launch. Verified directly against a
/// real spawned process before writing this: `getpgid(childPid)` reads back
/// equal to `childPid` and different from this process's own `getpgrp()`,
/// and a `kill(-pgid, SIGKILL)` afterwards took down a grandchild the child
/// itself had spawned.
public struct ChildSpawner: Sendable {
    public init() {}

    /// The one invariant both the spawn recovery path and the kill guard
    /// exist to protect: signalling `processGroupID` as a group must never
    /// reach this process's own group. Kept in a single place so the two
    /// call sites cannot drift — each still owns its own recovery (`spawn`
    /// kills the misgrouped pid directly and throws; `killProcessGroup`
    /// refuses and reports), but the check itself is asked once.
    ///
    /// `internal` rather than `private`, and tested directly: it sends no
    /// signal itself, so asserting it in isolation is the one way to prove
    /// the shared guard both recovery paths depend on without ever
    /// constructing a `SpawnedChild` whose group collides with our own and
    /// handing it to a signalling function — that test's failure mode would
    /// be `SIGKILL`ing the test runner's own group instead of a failed
    /// assertion.
    func wouldSignalOurOwnGroup(_ processGroupID: pid_t) -> Bool {
        processGroupID == getpgrp()
    }

    /// Which of `spawn`'s own four pipe descriptors should be explicitly
    /// closed in the child. Never 0, 1, or 2: `posix_spawn_file_actions`
    /// execute strictly in the order they were added, and 0 (stdin, wired to
    /// `/dev/null`), 1 (stdout) and 2 (stderr) are each the target of their
    /// own `adddup2`/`addopen` action elsewhere in `spawn`'s file-actions
    /// list. Any of these four descriptors can land on exactly 0, 1, or 2 —
    /// reachable whenever the host's own copy of that standard descriptor is
    /// free at `Pipe()` time (confirmed by the reviewer with a standalone
    /// repro mirroring `spawn`'s action order: a stdout read end landing on
    /// descriptor 0 was closed by this list's old unconditional close for
    /// it, undoing the `/dev/null` `addopen` installed for that same slot).
    /// Excluding 0, 1, and 2 here sidesteps the ordering question entirely
    /// rather than depending on getting the order right: nothing in this
    /// list ever names those three numbers, so whichever `adddup2`/`addopen`
    /// targets a given one of them is always the only action that touches
    /// it. Nothing leaks by skipping these closes — `adddup2` closes its
    /// target's previous occupant as part of duplicating onto it, and
    /// `addopen` reaches the same outcome by the same mechanism rather than
    /// by anything specific to `open(2)`: POSIX specifies it as opening the
    /// file and then duplicating that description onto the named descriptor,
    /// so it is `dup2`'s replacing semantics either way. By the time
    /// `posix_spawn`'s file actions finish, a slot's prior content is
    /// already gone whether or not this function named it.
    ///
    /// The `$0 > 2` filter also excludes negative values. That cannot arise
    /// from `spawn`'s own call site — `Foundation.Pipe` traps internally on a
    /// failed `pipe(2)`, so every descriptor reaching this function is valid —
    /// but it is stated because the filter reads as "standard descriptors
    /// only" and a future caller passing an unvalidated descriptor would get
    /// silent exclusion rather than a signal.
    ///
    /// `internal`, not `private`, and tested directly against synthetic
    /// descriptor numbers rather than real ones — the same reasoning as
    /// `wouldSignalOurOwnGroup`: exercising the 0/1/2 collision with real
    /// descriptors would mean deliberately closing this test process's own
    /// stdin, stdout, or stderr, a hazard to every other test sharing this
    /// process rather than a safe way to prove the decision.
    func descriptorsToCloseInChild(stdoutWriteFD: Int32, stderrWriteFD: Int32, stdoutReadFD: Int32, stderrReadFD: Int32) -> [Int32] {
        [stdoutWriteFD, stderrWriteFD, stdoutReadFD, stderrReadFD].filter { $0 > 2 }
    }

    /// Spawns `executablePath arguments...` in a new process group, with its
    /// stdout and stderr wired to pipes this process can read.
    ///
    /// If `POSIX_SPAWN_SETPGROUP` did not take effect, the child is already
    /// alive at the point this is discovered, in this process's own group.
    /// A hard trap here would kill the host and leave that child orphaned
    /// and re-parented to launchd — exactly the "orphaned model runtime"
    /// failure design D6 exists to prevent, manufactured by the guard meant
    /// to prevent it. There is no recovery advantage to crashing: the state
    /// is fully diagnosed (the pid is known, and it is known to be
    /// misgrouped), and `kill(pid, SIGKILL)` — a single pid, never a group —
    /// is unconditionally safe. So this recovers instead: kill the child by
    /// pid, reap it so it does not sit as a zombie for the host's lifetime,
    /// then throw.
    public func spawn(id: ChildID, executablePath: String, arguments: [String]) async throws(SpawnError) -> SpawnedChild {
        var attr: posix_spawnattr_t? = nil
        posix_spawnattr_init(&attr)
        defer { posix_spawnattr_destroy(&attr) }
        // Every descriptor this host holds — pipes, sockets, whatever a
        // future gateway client or credential store has open — is otherwise
        // inherited by every child it spawns. `Foundation.Pipe` sets no
        // `FD_CLOEXEC` on its descriptors (confirmed directly with
        // `fcntl(fd, F_GETFD)` against a fresh `Pipe()`), and this function's
        // own `addclose` calls below only ever named its own four. A
        // *sequentially* started child cannot inherit an earlier child's
        // pipe write end this way — this function closes the parent's copy
        // of its own write ends immediately after `posix_spawn` returns
        // (below), so by the time a later `spawn` call runs, that fd no
        // longer exists in the parent to be inherited. The exposure needs
        // *concurrent* spawns: another `spawn` call's `posix_spawn` running
        // inside the window between an in-flight `Pipe()` being created and
        // that call's own parent-side write-end close. That window is
        // reachable — a restart driven from `handleExit` can run
        // concurrently with another child's start or restart — though today,
        // with one child enabled, production impact is nil; `swift test`
        // running suites as concurrent tasks in one process makes the window
        // common, which is how the reviewer surfaced it (a stray descriptor
        // stalling `stdoutIsCapturedThroughThePipe` for 30s).
        // `POSIX_SPAWN_CLOEXEC_DEFAULT` (`sys/spawn.h`, verified exposed to
        // Swift via `Darwin`) closes everything not named by a file action
        // in the child at exec, closing that class of leak rather than one
        // instance of it.
        posix_spawnattr_setflags(
            &attr,
            Int16(
                POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK
                    | POSIX_SPAWN_CLOEXEC_DEFAULT
            )
        )
        posix_spawnattr_setpgroup(&attr, 0)

        // Without this, the child inherits this process's signal mask and
        // dispositions verbatim — so a `SIGTERM` this process happens to have
        // blocked (confirmed directly: `swift test`'s own runner blocks it)
        // is silently blocked in the child too, and in every grandchild it
        // spawns, since a blocked-or-non-default disposition inherits across
        // `posix_spawn`/`fork`/`exec` unless explicitly reset. That defeats
        // graceful termination (4.5) entirely: `killProcessGroup(signal:
        // SIGTERM)` would succeed at the `kill(2)` call, yet the signal would
        // never actually be delivered. `POSIX_SPAWN_SETSIGMASK` with an empty
        // mask unblocks everything; `POSIX_SPAWN_SETSIGDEF` with a full set
        // resets every signal's disposition to `SIG_DFL`, undoing any
        // handler this process (or its runtime) installed. A fresh child
        // process should never inherit either.
        var noBlockedSignals = sigset_t()
        sigemptyset(&noBlockedSignals)
        posix_spawnattr_setsigmask(&attr, &noBlockedSignals)
        var allSignalsDefaulted = sigset_t()
        sigfillset(&allSignalsDefaulted)
        posix_spawnattr_setsigdefault(&attr, &allSignalsDefaulted)

        var fileActions: posix_spawn_file_actions_t? = nil
        posix_spawn_file_actions_init(&fileActions)
        defer { posix_spawn_file_actions_destroy(&fileActions) }

        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        let stdoutWriteFD = stdoutPipe.fileHandleForWriting.fileDescriptor
        let stderrWriteFD = stderrPipe.fileHandleForWriting.fileDescriptor
        let stdoutReadFD = stdoutPipe.fileHandleForReading.fileDescriptor
        let stderrReadFD = stderrPipe.fileHandleForReading.fileDescriptor
        posix_spawn_file_actions_adddup2(&fileActions, stdoutWriteFD, 1)
        posix_spawn_file_actions_adddup2(&fileActions, stderrWriteFD, 2)
        // Close every pipe fd the child would otherwise still hold open
        // under its original number after dup2 has copied it onto 1/2 — a
        // spare copy of the write end left open in the child means our read
        // end never observes EOF once the child's real stdout/stderr copies
        // are closed.
        //
        // These closes are now redundant with `POSIX_SPAWN_CLOEXEC_DEFAULT`
        // above for any of the four that lands above descriptor 2 — none of
        // those is named by a `dup2`/`addopen` action under its own number,
        // so the flag alone would already close them at exec. Kept anyway,
        // deliberately: these are the specific descriptors this function
        // itself created and knows by number, closing them here does not
        // depend on an Apple-specific flag being honoured by whatever OS
        // version this runs on, and nothing is lost by stating explicitly,
        // for the fds this code controls, what the flag also guarantees more
        // broadly for everything else. `descriptorsToCloseInChild` excludes
        // 0, 1, and 2 from this list even so — see its own doc comment for
        // why closing one of those three would be actively harmful rather
        // than merely redundant.
        for descriptor in descriptorsToCloseInChild(
            stdoutWriteFD: stdoutWriteFD,
            stderrWriteFD: stderrWriteFD,
            stdoutReadFD: stdoutReadFD,
            stderrReadFD: stderrReadFD
        ) {
            posix_spawn_file_actions_addclose(&fileActions, descriptor)
        }

        // `POSIX_SPAWN_CLOEXEC_DEFAULT` above closes anything not named by a
        // file action — including this process's own stdin, fd 0, which
        // today the child inherits unchanged. Left unhandled, the child
        // would start with no descriptor 0 at all, so the next file it opens
        // silently lands on 0, and anything it writes believing it is
        // writing to stdin corrupts that file instead. POSIX programs assume
        // 0, 1 and 2 are open; wire fd 0 to `/dev/null` explicitly so the
        // child gets a well-defined, harmless descriptor rather than an
        // empty slot.
        //
        // Added last, after every `adddup2`/`addclose` above:
        // `posix_spawn_file_actions` execute strictly in the order they were
        // added, and `descriptorsToCloseInChild` already guarantees none of
        // them ever names descriptor 0 — so nothing after this point can
        // touch slot 0 again regardless. Ordering this last is a second,
        // independent guard against the same class of mistake, not load-
        // bearing on its own: even if a future change reintroduced an
        // unconditional close somewhere in this file, this `addopen` being
        // last still wins.
        posix_spawn_file_actions_addopen(&fileActions, 0, "/dev/null", O_RDONLY, 0)

        let argv = [executablePath] + arguments
        var cArgs: [UnsafeMutablePointer<CChar>?] = argv.map { strdup($0) }
        cArgs.append(nil)
        defer { cArgs.forEach { if let pointer = $0 { free(pointer) } } }

        var cEnv: [UnsafeMutablePointer<CChar>?] = ProcessInfo.processInfo.environment.map { strdup("\($0.key)=\($0.value)") }
        cEnv.append(nil)
        defer { cEnv.forEach { if let pointer = $0 { free(pointer) } } }

        var pid: pid_t = 0
        let status = posix_spawn(&pid, executablePath, &fileActions, &attr, &cArgs, &cEnv)

        stdoutPipe.fileHandleForWriting.closeFile()
        stderrPipe.fileHandleForWriting.closeFile()

        guard status == 0 else {
            throw SpawnError.posixSpawnFailed(errno: status)
        }

        let processGroupID = getpgid(pid)
        guard !wouldSignalOurOwnGroup(processGroupID) else {
            kill(pid, SIGKILL)
            _ = await awaitExit(of: pid)
            throw SpawnError.childInheritedOurProcessGroup(pid: pid)
        }

        return SpawnedChild(
            id: id,
            pid: pid,
            processGroupID: processGroupID,
            standardOutput: stdoutPipe.fileHandleForReading,
            standardError: stderrPipe.fileHandleForReading
        )
    }

    /// Signals `child`'s entire process group, so no process it spawned
    /// (a grandchild, in section 5.1 terms) survives it.
    ///
    /// Guarded the same invariant `spawn` is, and deliberately re-checked
    /// here rather than trusted from the spawn site: `SpawnedChild.init` is
    /// public, so a `child.processGroupID` that collides with our own group
    /// is reachable, not merely hypothetical — this is not trusted from
    /// further away. A signal is irreversible, so refusing costs nothing but
    /// the signal itself, and this returns `false` rather than trapping.
    /// **Not `@discardableResult`**: a caller signalling a child must look
    /// at the result — silently discarding it is the same silent-failure
    /// hazard this whole remediation exists to close. `HostSupervisor
    /// .shutdown()` is the one caller today, and surfaces a refusal as the
    /// `ChildID` it returns.
    public func killProcessGroup(of child: SpawnedChild, signal: Int32 = SIGKILL) -> Bool {
        guard !wouldSignalOurOwnGroup(child.processGroupID) else {
            return false
        }
        // `kill`'s own return is intentionally unchecked: an `ESRCH` here
        // means the group had already exited, which is not a failure this
        // caller needs to know about — only "we refused to signal our own
        // group" (returned above) is.
        kill(-child.processGroupID, signal)
        return true
    }
}
