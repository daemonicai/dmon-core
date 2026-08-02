import Foundation

/// A child process this host itself launched via `ChildSpawner`.
///
/// Only a spawned child carries a process-group id: an adopted child (see
/// `ChildStartOutcome.adopted`) has no value of this type at all, so there is
/// nothing to signal it through. "Adoption is exempt" (Requirement: Spawned
/// children are killed by process group on exit) therefore holds by
/// construction rather than by a `wasAdopted` flag every kill site would
/// otherwise have to remember to check.
public struct SpawnedChild: Sendable {
    public let id: ChildID
    public let pid: pid_t

    /// The process group this child (and anything it itself spawns) belongs
    /// to. Always distinct from this process's own `getpgrp()`: `ChildSpawner.spawn`
    /// checks that before this value is ever produced, and recovers rather
    /// than producing a `SpawnedChild` at all if it does not hold.
    public let processGroupID: pid_t

    /// The read end of the pipe wired to the child's stdout (section 5.1).
    public let standardOutput: FileHandle

    /// The read end of the pipe wired to the child's stderr (section 5.1).
    public let standardError: FileHandle

    public init(id: ChildID, pid: pid_t, processGroupID: pid_t, standardOutput: FileHandle, standardError: FileHandle) {
        self.id = id
        self.pid = pid
        self.processGroupID = processGroupID
        self.standardOutput = standardOutput
        self.standardError = standardError
    }
}

/// How a spawned child's process actually ended, as observed by `awaitExit(of:)`.
public struct ChildExitStatus: Hashable, Sendable {
    public let pid: pid_t
    public let exitCode: Int32?
    public let terminatingSignal: Int32?

    public init(pid: pid_t, exitCode: Int32?, terminatingSignal: Int32?) {
        self.pid = pid
        self.exitCode = exitCode
        self.terminatingSignal = terminatingSignal
    }
}
