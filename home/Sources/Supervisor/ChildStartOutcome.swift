/// The result of `ChildStartCoordinator.start(_:)` for one descriptor.
public enum ChildStartOutcome: Sendable {
    /// The endpoint already answered its health check: an existing process
    /// was adopted, and nothing was spawned.
    case adopted

    /// Nothing answered the health check (or the policy is `.spawnOnly`),
    /// and a fresh process was launched successfully.
    case spawned(SpawnedChild)

    /// `launch.candidates` is empty: this child's launch path is not a
    /// decided fact yet (true today for both mlx runtimes and the speech
    /// sidecar — see `ChildLaunch`). Kept distinct from `.executableNotResolved`
    /// so a human reading the result can tell "not decided" apart from
    /// "misconfigured".
    case launchNotDecided

    /// One or more candidates were declared, but none resolved to an
    /// executable file. Unlike dmonium's `ServerProcessManager.start()`,
    /// which silently sets `isRunning = false` and returns on this exact
    /// condition, this is a distinguishable, inspectable outcome precisely
    /// so a misconfiguration does not go unnoticed.
    case executableNotResolved

    /// A candidate resolved, but `posix_spawn` itself failed.
    case spawnFailed(SpawnError)
}
