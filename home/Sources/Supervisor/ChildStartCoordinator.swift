/// Reattach-first startup (design D6): for each descriptor, health-check its
/// endpoint before ever spawning, so a live child — mlx's multi-gigabyte
/// model runtime, notably — survives a restart of the host instead of being
/// started a second time underneath the one already running.
public struct ChildStartCoordinator: Sendable {
    private let healthChecker: HealthChecker
    private let resolver: ExecutableResolver
    private let spawner: ChildSpawner

    public init(
        healthChecker: HealthChecker = HealthChecker(),
        resolver: ExecutableResolver = ExecutableResolver(),
        spawner: ChildSpawner = ChildSpawner()
    ) {
        self.healthChecker = healthChecker
        self.resolver = resolver
        self.spawner = spawner
    }

    /// Adopts `descriptor` if its `adoptionPolicy` is `.adoptOrSpawn` and its
    /// declared endpoint already answers; otherwise resolves an executable
    /// from `descriptor.launch` and spawns it.
    ///
    /// Adoption is decided by this health check alone — never by a PID file.
    /// dmonium's `ServerProcessManager.adoptIfRunning()` reads a PID file
    /// and probes `kill(pid, 0)`; that mechanism depends on a file this host
    /// never writes, and can only tell "a process with this pid still
    /// exists" apart from "a process that is actually answering requests" —
    /// which is what "adopted" needs to mean here. Probing the declared
    /// endpoint directly needs no PID file at all, and is exactly what the
    /// spec scenario asks for ("health-check the child's known endpoint
    /// first"). This divergence was flagged in the review of the health-check
    /// block (4.3) and lands here, where adoption is actually implemented.
    public func start(_ descriptor: ChildDescriptor) async -> ChildStartOutcome {
        if descriptor.adoptionPolicy == .adoptOrSpawn {
            let health = await healthChecker.check(descriptor.healthCheck, timeout: descriptor.healthCheckTimeout)
            if health == .healthy {
                return .adopted
            }
        }

        switch resolver.resolve(descriptor.launch.candidates) {
        case .notDecided:
            return .launchNotDecided
        case .unresolved:
            return .executableNotResolved
        case .resolved(let path):
            do {
                let child = try await spawner.spawn(id: descriptor.id, executablePath: path, arguments: descriptor.launch.arguments)
                return .spawned(child)
            } catch {
                return .spawnFailed(error)
            }
        }
    }
}
