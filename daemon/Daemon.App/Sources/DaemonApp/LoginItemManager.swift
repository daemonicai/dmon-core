import Foundation
import ServiceManagement

/// Manages the SMAppService login-item registration (§10.4, ADR-D7).
/// First-launch default: registered once, idempotent via a UserDefaults gate.
@MainActor
final class LoginItemManager: ObservableObject {

    @Published var isEnabled: Bool = UserDefaults.standard.bool(forKey: Keys.enabled)

    // MARK: - UserDefaults keys

    private enum Keys {
        static let configured = "loginItemConfigured"
        static let enabled    = "loginItemEnabled"
    }

    // MARK: - First-launch registration

    /// Call once at app startup. Registers the login item on first launch only;
    /// subsequent launches honour the stored toggle state (idempotent).
    func registerOnFirstLaunchIfNeeded() {
        guard !UserDefaults.standard.bool(forKey: Keys.configured) else { return }
        try? SMAppService.mainApp.register()
        UserDefaults.standard.set(true, forKey: Keys.enabled)
        UserDefaults.standard.set(true, forKey: Keys.configured)
        isEnabled = true
    }

    // MARK: - Toggle

    /// Sets the login-item registration state and persists it.
    func setEnabled(_ enabled: Bool) {
        if enabled {
            try? SMAppService.mainApp.register()
        } else {
            try? SMAppService.mainApp.unregister()
        }
        isEnabled = enabled
        UserDefaults.standard.set(enabled, forKey: Keys.enabled)
    }
}
