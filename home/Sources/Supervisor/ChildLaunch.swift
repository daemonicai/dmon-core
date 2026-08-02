/// Where a child's executable might be found, in priority order. Mirrors
/// dmonium's `NetworkManager.networkCandidates`: an env-var override, then a
/// home-relative default; the first one that resolves to an executable file wins.
/// Resolving a source into an actual path — expanding `~`, probing `PATH`,
/// checking executability — is a later block's concern (task 4.7).
public enum ExecutableSource: Hashable, Sendable {
    /// Read the path from this environment variable.
    case environmentVariable(String)

    /// A path relative to the user's home directory, e.g. `.dotnet/tools/ndmon`.
    case homeRelativePath(String)

    /// A fully-qualified path, used verbatim.
    case absolutePath(String)
}

/// How a supervised child is launched, when adoption does not find it already
/// running.
///
/// `candidates` is tried in order; the first source that resolves to an
/// executable file wins. An empty list means this change has not decided how to
/// launch the child — true today for the mlx runtimes (ADR-034 runs them from a
/// uv venv, not a bare command) and the speech sidecar (no implementation yet,
/// design D7) — and is itself meaningful data, not a placeholder.
public struct ChildLaunch: Hashable, Sendable {
    public let candidates: [ExecutableSource]
    public let arguments: [String]

    public init(candidates: [ExecutableSource] = [], arguments: [String] = []) {
        self.candidates = candidates
        self.arguments = arguments
    }
}
