#if canImport(Darwin)
import Darwin
#endif

/// Why `ChildSpawner.spawn` could not produce a `SpawnedChild`.
public enum SpawnError: Error, Hashable, Sendable {
    /// `posix_spawn` itself returned non-zero. POSIX's `posix_spawn` returns
    /// the error number directly rather than setting the global `errno`, so
    /// this value *is* that errno, not a copy of the global.
    case posixSpawnFailed(errno: Int32)

    /// `posix_spawn` succeeded, but `POSIX_SPAWN_SETPGROUP` did not take
    /// effect: the child came up in this process's own group instead of a
    /// new one. By the time this is discovered the child is already alive,
    /// so `spawn` has already recovered from it — killed `pid` directly
    /// (never as a group, which would reach this process too) and reaped
    /// it — before throwing this. There is no orphan left behind.
    case childInheritedOurProcessGroup(pid: pid_t)
}
