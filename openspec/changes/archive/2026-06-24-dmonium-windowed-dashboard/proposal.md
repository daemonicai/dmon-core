## Why

`dmonium` (`daemon/Daemon.App`) ships today as a menu-bar-only accessory app: a single `MenuBarExtra` status icon with a dropdown of health rows and a few action buttons, no dock icon and no window. On a real macOS menu bar — especially a notched MacBook where bar real estate is scarce — the icon is routinely hidden behind overflow, so the supervisor you most need to *find* when something is wrong is the one you can't see. The control surface for an always-on personal-assistant daemon should be a real, discoverable window, not a tray icon competing for menu-bar space.

## What Changes

- **dmonium becomes window-primary.** A real `WindowGroup` dashboard is the primary surface, with a Dock icon and Cmd-Tab presence (`.regular` activation policy). The menu-bar icon is demoted to an **optional** glance affordance (`MenuBarExtra(isInserted:)` bound to a persisted setting, default **off**).
- **A windowed dashboard** (`NavigationSplitView`: Status / Services / Settings sections) replaces the dropdown menu:
  - Health **grid** with each component's status and **last-poll timestamp** (new — requires a small `ComponentHealth.lastUpdated` field, stamped by each publisher).
  - **Per-service** start / stop / **restart** controls (the `restart()` verbs already exist on `GatewayManager`, `ServiceManager`, `ServerProcessManager`), plus the existing Sync-Calendar and Bring-Tailscale-up actions.
  - The Settings panel moves **into the window** as a section (retiring the separate SwiftUI `Settings` scene; ⌘, selects the section). Keychain-backed secret handling is unchanged.
- **The supervision lifecycle is decoupled from view/scene lifecycle.** A single `@MainActor` owner (`DaemonController`) owns all managers + the health registry and exposes one idempotent `bootstrap()`, invoked once from `applicationDidFinishLaunching` — never from a view `.task`. This fixes a latent double-fire/subscription-leak hazard that a naive `WindowGroup` port would expose (window reopen re-runs `.task`), and guarantees the always-on daemon supervises its children at login even when no window is drawn.
- **`close ≠ quit`.** `applicationShouldTerminateAfterLastWindowClosed → false`: closing the dashboard hides it (the Gateway and supervised servers keep running); the window reopens from the Dock; only Quit (app menu) performs the existing orderly child teardown.
- **Dock-icon rollup tint** preserves the one genuinely-useful property of the menu-bar icon — at-a-glance green/amber/red health — without consuming any menu-bar space. The same `HealthRegistry.rollupColor` drives the window header, the Dock icon, and the optional tray icon.
- **ADR-028 amendment (framing only).** ADR-028 describes dmonium as an "always-on, no-dock host"; this change relaxes that *rationale* to "window-primary, Dock-present." No numbered ADR-028 decision (bucket placement, Swift-in-repo, release artifacts, dcal rename) changes, so this is a one-line amendment note, **not** a superseding ADR.

No change to dmon-core, the protocol, the Gateway, or any `Dmon.*` package. This is `daemon/Daemon.App`-local.

## Capabilities

### New Capabilities

_(none — dmonium already exists; this change modifies its presentation and lifecycle requirements within the existing `daemon-host` capability.)_

### Modified Capabilities

- `daemon-host`: 
  - **Menu bar application shows live Gateway status** → generalised to a **window/Dock** status surface (the green/amber/red rollup rule is preserved verbatim; the menu-bar icon becomes an optional, default-off surface; the Dock icon is tinted by the rollup).
  - **Gateway process is started on app launch and restarted on crash** → clarified that startup/supervision is **window-independent** (driven once from `applicationDidFinishLaunching`, not a view appearance) and that closing the window does not stop supervision.
  - **Settings panel manages all Daemon configuration** → the settings surface is an **in-window section** rather than a separate `Settings` scene (configuration/Keychain semantics unchanged).
  - **Menu provides a best-effort Bring Tailscale up action** → the action (plus new per-service start/stop/restart and the existing Sync-Calendar action) is provided by the **window dashboard**, not the menu dropdown.
  - **Unified health surface aggregates all components** → additively surfaces each component's **last-poll timestamp** in the dashboard grid (rollup contract unchanged).

## Impact

- **Code (`daemon/Daemon.App` only):**
  - New `DaemonController` owner (`@MainActor ObservableObject`) holding the managers + `HealthRegistry`, with a once-guarded `bootstrap()`/`shutdown()`.
  - `DaemonApp.swift`: `MenuBarExtra`-primary scene → `WindowGroup`-primary + optional `MenuBarExtra(isInserted:)`; `AppDelegate` gains activation policy (`.regular`), `applicationShouldTerminateAfterLastWindowClosed`, and `applicationDidFinishLaunching → controller.bootstrap()`.
  - New dashboard views (`NavigationSplitView` + Status / Services / Settings sections); `MenuBarView` content reused for the optional glance surface.
  - `ComponentHealth` gains `lastUpdated: Date?`; each monitor/manager stamps it on publish.
  - Dock-icon tinting from `rollupColor`.
  - A persisted "show menu-bar icon" setting (default off).
- **Tests:** new/updated `DaemonAppTests` — `bootstrap()` idempotence, rollup→Dock-tint mapping, `lastUpdated` stamping, settings-toggle persistence. Existing 31 tests stay green.
- **Docs/specs:** `daemon-host` spec deltas (above); one-line ADR-028 amendment note; `daemon/README` note that dmonium is window-primary with an optional tray.
- **No change** to dmon-core / protocol / Gateway / `Dmon.*` packages, the build seam (`make daemon-app`), or `Everything.slnx`.
