import Foundation
import Combine

// MARK: - RollupColor

/// The three states an icon rollup can produce.
enum RollupColor: Equatable {
    case green
    case amber
    case red
}

// MARK: - HealthRegistry

/// Observable ordered collection of per-component health snapshots.
/// Each manager publishes its own `ComponentHealth`; the registry collects them
/// and derives an aggregate `rollupColor` for use by the icon (group 6 / task 6.2).
///
/// Registration order determines the stable display order in the status grid:
///   Network → Dcal → Dmail → Tailscale → Calendar Sync
@MainActor
final class HealthRegistry: ObservableObject {

    /// Ordered list of component snapshots; each slot is keyed by its registration `order`.
    @Published private(set) var components: [ComponentHealth] = []

    /// Aggregate icon colour derived via `rollup(networkStopped:components:)`.
    /// Updated whenever any component changes.
    ///
    /// The menu-bar icon's `foregroundStyle` is wired to this in `DaemonApp`.
    /// The `networkStopped` flag is fed separately to preserve the Network's
    /// honest per-component status while still forcing red when it is down.
    @Published private(set) var rollupColor: RollupColor = .red

    // Internal: tracks whether the Network is stopped (feeds rollup's special param).
    // Set by the wiring in DaemonController.bootstrap(); kept separate from the component list.
    private var networkStopped: Bool = true

    private var cancellables: Set<AnyCancellable> = []

    // MARK: - Registration

    /// Subscribe a component's `ComponentHealth` publisher into the registry.
    /// `order` is a stable sort key so the display list remains in intentional order
    /// regardless of which subscriptions fire first.
    ///
    /// Call once per component in `DaemonController.bootstrap()`.
    func register<P: Publisher>(
        publisher: P,
        order: Int
    ) where P.Output == ComponentHealth, P.Failure == Never {
        publisher
            .receive(on: RunLoop.main)
            .sink { [weak self] health in
                guard let self else { return }
                self.upsert(health, order: order)
            }
            .store(in: &cancellables)
    }

    // MARK: - Network-stopped feed

    /// Called by DaemonController whenever the Network's `isRunning` changes.
    /// Triggers a rollup recompute.
    func setNetworkStopped(_ stopped: Bool) {
        networkStopped = stopped
        recomputeRollup()
    }

    /// Subscribe a `Bool` publisher (true = network is stopped) to keep the
    /// rollup's special first-check in sync without polluting the component list.
    /// Call once in `DaemonController.bootstrap()` with `network.$isRunning.map { !$0 }`.
    func observeNetworkStopped<P: Publisher>(
        _ publisher: P
    ) where P.Output == Bool, P.Failure == Never {
        publisher
            .receive(on: RunLoop.main)
            .sink { [weak self] stopped in
                self?.setNetworkStopped(stopped)
            }
            .store(in: &cancellables)
    }

    // MARK: - Internal

    /// Order-keyed slot storage. Keys are stable integers assigned at registration.
    private var slots: [Int: ComponentHealth] = [:]

    private func upsert(_ health: ComponentHealth, order: Int) {
        slots[order] = health
        components = slots.sorted(by: { $0.key < $1.key }).map(\.value)
        recomputeRollup()
    }

    private func recomputeRollup() {
        rollupColor = Self.rollup(networkStopped: networkStopped, components: components)
    }

    // MARK: - Pure rollup (testable without ObservableObject; group 8 / task 8.3)

    /// Derives the aggregate icon colour from the Network-stopped flag and the
    /// full component list.
    ///
    /// Decision 3 / spec rollup contract:
    ///   1. Network stopped → red (regardless of other components).
    ///   2. Else any component `down` → red.
    ///   3. Else any component `degraded` or `unknown` → amber.
    ///   4. Else green.
    ///
    /// The Network's own `ComponentHealth` is classified honestly (ok/down/unknown);
    /// the `networkStopped` param is what forces red — do NOT encode this by mapping
    /// the Network to `down` in its `ComponentHealth`. Feeds
    /// `networkStopped: !network.isRunning` when wiring the icon.
    static func rollup(networkStopped: Bool, components: [ComponentHealth]) -> RollupColor {
        if networkStopped { return .red }
        if components.contains(where: { $0.status == .down }) { return .red }
        if components.contains(where: { $0.status == .degraded || $0.status == .unknown }) { return .amber }
        return .green
    }
}
