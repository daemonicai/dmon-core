import Foundation

/// Resolves a `ChildLaunch`'s ordered candidates into the executable path
/// `ChildSpawner` should launch — mirroring dmonium's `NetworkManager`
/// candidate order (an env-var override, then a home-relative default): the
/// first candidate that resolves to an executable file wins.
///
/// Every filesystem-touching step is an injected seam, so tests can exercise
/// every branch — including "resolves to nothing" — without depending on
/// this process's real environment, home directory, or filesystem.
public struct ExecutableResolver: Sendable {
    /// What `resolve(_:)` found.
    ///
    /// `.notDecided` and `.unresolved` are kept distinct on purpose:
    /// `candidates.isEmpty` is a *static* fact ("this child's launch path is
    /// not a decided fact yet" — true today for both mlx runtimes and the
    /// speech sidecar), while trying every declared candidate and finding
    /// none executable is a *runtime* outcome ("misconfigured"). Collapsing
    /// the two into one "resolution failed" case would make a human reading
    /// the result unable to tell design intent apart from a broken install.
    public enum Resolution: Hashable, Sendable {
        case notDecided
        case resolved(path: String)
        case unresolved
    }

    private let environment: @Sendable () -> [String: String]
    private let homeDirectory: @Sendable () -> String
    private let isExecutableFile: @Sendable (String) -> Bool

    public init(
        environment: @escaping @Sendable () -> [String: String] = { ProcessInfo.processInfo.environment },
        homeDirectory: @escaping @Sendable () -> String = { NSHomeDirectory() },
        isExecutableFile: @escaping @Sendable (String) -> Bool = { FileManager.default.isExecutableFile(atPath: $0) }
    ) {
        self.environment = environment
        self.homeDirectory = homeDirectory
        self.isExecutableFile = isExecutableFile
    }

    public func resolve(_ candidates: [ExecutableSource]) -> Resolution {
        guard !candidates.isEmpty else { return .notDecided }
        for candidate in candidates {
            if let path = path(for: candidate), isExecutableFile(path) {
                return .resolved(path: path)
            }
        }
        return .unresolved
    }

    private func path(for source: ExecutableSource) -> String? {
        switch source {
        case .environmentVariable(let key):
            return environment()[key]
        case .homeRelativePath(let relativePath):
            // There is no `~` in the stored value (`ChildLaunch`'s own
            // documentation): expansion against the real home directory is
            // this resolver's job, not the descriptor's.
            return (homeDirectory() as NSString).appendingPathComponent(relativePath)
        case .absolutePath(let path):
            return path
        }
    }
}
