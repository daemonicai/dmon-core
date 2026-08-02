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
        posix_spawnattr_setflags(
            &attr,
            Int16(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK)
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
        posix_spawn_file_actions_adddup2(&fileActions, stdoutWriteFD, 1)
        posix_spawn_file_actions_adddup2(&fileActions, stderrWriteFD, 2)
        // Close every pipe fd the child would otherwise still hold open
        // under its original number after dup2 has copied it onto 1/2 — a
        // spare copy of the write end left open in the child means our read
        // end never observes EOF once the child's real stdout/stderr copies
        // are closed.
        posix_spawn_file_actions_addclose(&fileActions, stdoutWriteFD)
        posix_spawn_file_actions_addclose(&fileActions, stderrWriteFD)
        posix_spawn_file_actions_addclose(&fileActions, stdoutPipe.fileHandleForReading.fileDescriptor)
        posix_spawn_file_actions_addclose(&fileActions, stderrPipe.fileHandleForReading.fileDescriptor)

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
    /// **Not `@discardableResult`**: a caller signalling one child on its own
    /// must look at the result, exactly like `killProcessGroups` below must
    /// look at its `[ChildID]` — silently discarding either is the same
    /// silent-failure hazard this whole remediation exists to close.
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

    /// Kills every given child's process group, continuing past any refusal
    /// rather than stopping at the first one: at app-exit, children
    /// 2...n still need to be signalled even if child 1's group turned out
    /// to be unsafe. Because this accepts only `[SpawnedChild]`, an adopted
    /// child — which has no `SpawnedChild` value to begin with — cannot be
    /// passed here even by mistake: "kill everything this host spawned, and
    /// leave everything it adopted alone" holds by construction.
    ///
    /// Returns the ids of any children this refused to signal. Deliberately
    /// not `@discardableResult`: the supervision spec requires failure to be
    /// surfaced rather than silently avoided, so a caller (the app-exit
    /// wiring, task 4.7) must at least acknowledge an empty-vs-nonempty
    /// result, even if only to log it.
    ///
    /// Note for whoever adds a second refusal reason (not 4.4, not 4.5, as
    /// far as either touches this file): `[ChildID]` is sufficient only
    /// because `wouldSignalOurOwnGroup` is the *only* way this can refuse
    /// today. A second reason would need the return type to carry which
    /// reason applied per child, not just the id.
    public func killProcessGroups(of children: [SpawnedChild], signal: Int32 = SIGKILL) -> [ChildID] {
        var refused: [ChildID] = []
        for child in children {
            if !killProcessGroup(of: child, signal: signal) {
                refused.append(child.id)
            }
        }
        return refused
    }
}
