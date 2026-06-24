## 1. DaemonController owner + window-independent bootstrap

- [x] 1.1 Add `ComponentHealth.lastUpdated: Date?` (default `nil`); update the `init` and keep `name/status/detail` unchanged so existing rollup tests stay valid.
- [x] 1.2 Stamp `lastUpdated` at publish time in every publisher (`GatewayManager`, `TailscaleMonitor`, `DcalHealthMonitor`, `DmailHealthMonitor`, `EndpointHealthProbe`, `ServiceManager`).
- [x] 1.3 Create `DaemonController` (`@MainActor final class … : ObservableObject`) that owns the managers + `HealthRegistry` and exposes `bootstrap()` (guarded by a private `hasBootstrapped` flag) and `shutdown()`. Move the manager `start()` calls, the nine `healthRegistry.register(...)` calls, `observeGatewayStopped`, and the monitor/probe `start()` calls into `bootstrap()`.
- [x] 1.4 Confirm `TailscaleMonitor`/`DcalHealthMonitor`/`DmailHealthMonitor` `start()` are re-entry-safe (`guard pollTask == nil` pattern, as `EndpointHealthProbe` already is); normalise any that are not.
- [x] 1.5 Replace the ~11 `@StateObject`s + the `.task { }` startup block in `DaemonApp.swift` with a single `@StateObject private var controller = DaemonController()`.

## 2. Activation policy, close ≠ quit, lifecycle wiring

- [x] 2.1 In `AppDelegate.applicationDidFinishLaunching`, call `NSApp.setActivationPolicy(.regular)` and `controller.bootstrap()` (window-independent, once).
- [x] 2.2 Implement `applicationShouldTerminateAfterLastWindowClosed(_:) -> false` so closing the window keeps supervision running.
- [x] 2.3 Route `applicationWillTerminate` to `controller.shutdown()` (the existing Gateway/Dcal/Dmail teardown + PID-file clearing).
- [x] 2.4 Verify reopen behaviour: closing then reopening the window (Dock click) re-binds the existing `controller` with no re-bootstrap and no duplicate health subscriptions.

## 3. Windowed dashboard surface

- [x] 3.1 Replace the `MenuBarExtra`-primary scene with `WindowGroup { DashboardView().environmentObject(controller) }`; remove the separate `Settings` scene.
- [x] 3.2 Build `DashboardView` as a `NavigationSplitView` with sidebar sections Status / Services / Settings (structured to admit a future section without redesign).
- [x] 3.3 Status section: a health grid rendering one row per `HealthRegistry` component with status, detail, and a relative last-updated time.
- [x] 3.4 Services section: per-service start/stop/restart controls (Gateway, Dcal, Dmail) plus the existing Sync-Calendar and Bring-Tailscale-up actions.
- [x] 3.5 Settings section: move `SettingsView` content into the window section; rebind ⌘, to select it; keep Keychain/`config.yaml`/Gateway-restart-on-save semantics unchanged.

## 4. Dock-icon rollup tint + optional menu-bar surface

- [x] 4.1 Drive the Dock icon (and the window status surface) from `HealthRegistry.rollupColor` (green/amber/red), preserving the at-a-glance signal.
- [x] 4.2 Add a persisted "show menu-bar icon" setting (default **off**); add `MenuBarExtra(isInserted: $showTrayIcon)` reusing the existing `MenuBarView` content as the glance surface, reading the same `controller`.

## 5. Tests

- [x] 5.1 `DaemonController.bootstrap()` idempotence: calling it twice starts processes/subscriptions once (no duplicate health subscriptions, no second process launch).
- [x] 5.2 Rollup → presentation mapping: `rollupColor` cases map to the expected Dock/window/tray colour states (extend the existing rollup tests; keep them green).
- [ ] 5.3 `ComponentHealth.lastUpdated` is stamped on publish for each publisher.
- [x] 5.4 Show-menu-bar-icon setting persists and defaults to off.
- [ ] 5.5 Full `DaemonAppTests` suite green (existing 31 + new).

## 6. Docs & ADR

- [ ] 6.1 Add the one-line ADR-028 framing amendment (dmonium is window-primary with a Dock icon and optional menu-bar glance; "no-dock host" framing superseded). No superseding ADR unless the reviewer judges otherwise (stop-and-ask → ADR-033).
- [ ] 6.2 Update `daemon/README` to note dmonium is window-primary with an optional, default-off menu-bar icon.
