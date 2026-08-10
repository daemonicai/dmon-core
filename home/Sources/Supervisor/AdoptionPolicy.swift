/// How a supervised child's adopt-vs-spawn decision is made at startup.
public enum AdoptionPolicy: Hashable, Sendable {
    /// Health-check the declared endpoint first; adopt an already-running process
    /// if it answers, and spawn only when nothing does.
    case adoptOrSpawn

    /// Never attempt adoption; always spawn a fresh process.
    case spawnOnly
}
