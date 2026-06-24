## Context

`dmonium` (`daemon/Daemon.App`, ADR-028) is a Swift Package executable that supervises the Daemon's backing processes (Gateway, Dcal server, Dmail server), monitors Tailscale and the model-runner endpoints, aggregates everything into a `HealthRegistry` rollup, and edits configuration. Today its only surface is a SwiftUI `MenuBarExtra` (a status icon + dropdown) plus a separate `Settings` scene; there is no window and no Dock icon (SwiftUI infers accessory/agent activation from a scene set with no `WindowGroup`).

Two facts shape this design, both established by reading the current code:

1. **The content is already decoupled from the shell.** `MenuBarView` is pure presentation over `@EnvironmentObject` managers; the managers are `@StateObject`s declared on the `App` struct, not on the menu scene. Moving to a window is largely a *Scene*-level change — the supervision/health machinery is untouched.
2. **Supervision startup currently lives in a view `.task`.** `DaemonApp.swift` wires everything — `start()` on every manager, nine `healthRegistry.register(...)` calls, the quit-teardown delegate — inside `.task { }` attached to the `MenuBarExtra` content view. This is safe *only because* a `MenuBarExtra` content view is effectively permanent. A closable, recreatable `WindowGroup` re-runs `.task` on every reopen.

Constraints: `daemon/Daemon.App`-local (no dmon-core/protocol/Gateway/`Dmon.*` change); built via `make daemon-app` (outside `Everything.slnx`); macOS 14+; existing 31 `DaemonAppTests` stay green; `TreatWarningsAsErrors` discipline applies to the .NET side but not Swift — Swift code should still build clean.

## Goals / Non-Goals

**Goals:**
- A discoverable, window-primary control surface with a Dock icon and Cmd-Tab presence, immune to menu-bar overflow.
- A real dashboard: health grid with last-poll times, per-service start/stop/restart, in-window settings.
- Supervision lifecycle fully decoupled from window/scene lifecycle — children supervised at login even with no window drawn; `close ≠ quit`.
- Preserve the menu-bar icon's only genuinely-useful property (at-a-glance rollup colour) via a Dock-icon tint, with the menu-bar icon retained as an optional, default-off surface.
- No regression: the rollup contract, crash-restart/back-off, PID adoption, Keychain secrets, and login-item registration all keep working.

**Non-Goals:**
- No dmon-core, protocol, Gateway, or `Dmon.*` package change. No new wire command.
- No new ADR (ADR-028 gets a one-line framing amendment only — see D7).
- No multi-window / multi-session / tabbed surface; one dashboard window.
- No log-streaming/tail pane in this change (a natural follow-on; the `NavigationSplitView` leaves room for it).
- No change to *what* settings exist or *where* they persist (Keychain + `~/.dmon/config.yaml`); only their presentation surface moves.
- No code-signing/notarisation work (carried, deferred, by ADR-028).

## Decisions

### D1: A single `DaemonController` owner, bootstrapped off the window

Introduce a `@MainActor final class DaemonController: ObservableObject` that *owns* the managers (`GatewayManager`, `ServiceManager` ×2, the monitors, the endpoint probes) and the `HealthRegistry`, replacing the ~11 `@StateObject`s scattered on the `App` struct with **one** `@StateObject private var controller = DaemonController()`. It exposes:

- `bootstrap()` — starts every manager, registers all health publishers (with their stable orders), wires `observeGatewayStopped`, and starts the monitors. Guarded by a private `hasBootstrapped` flag so it is a no-op if ever called again.
- `shutdown()` — the existing orderly teardown (`stop()` on Gateway/Dcal/Dmail, clear PID files).

`bootstrap()` is invoked **once**, from `AppDelegate.applicationDidFinishLaunching` — a window-independent, once-per-process hook — **not** from any view `.task`. The controller, being a single `@StateObject` on the `App`, lives for the whole process and is injected into both the `WindowGroup` root and the optional `MenuBarExtra` as one `.environmentObject`.

- **Why:** This is the structural fix for the re-fire hazard, not a per-manager patch. It also satisfies the always-on requirement: a login-item launch (`SMAppService`) that comes up with no visible window still supervises children, because startup is in `didFinishLaunching`, not a view.
- **Alternative — keep the scattered `@StateObject`s and move the `.task` to the `WindowGroup` root:** rejected. The view is now volatile; reopening the window re-runs `.task`. Even with per-manager idempotency it re-subscribes the health publishers (see D2). Relying on idempotency-by-accident is the wrong model.
- **Alternative — bootstrap in `App.init()`:** rejected. `@StateObject` is not safe to touch in `init`; `didFinishLaunching` is the honest once-only hook and is already where the delegate lives.

### D2: Registration happens exactly once — closing the latent subscription leak

`HealthRegistry.register(publisher:order:)` stores each subscription in a `cancellables` set and writes into an `order`-keyed slot dict. A *second* `register()` for the same order does **not** duplicate the visible row (the dict overwrites the slot) but **does** add a second live Combine sink that is never cancelled — an unbounded subscription/CPU leak on every window reopen. Moving all `register(...)` calls into `bootstrap()` (D1) means they run exactly once, structurally eliminating the leak. No change to `HealthRegistry` itself is required, though `bootstrap()`'s guard is the guarantee.

- **Note:** `ServerProcessManager.start()` (PID-file adoption) and `EndpointHealthProbe.start()` (`guard pollTask == nil`) are already re-entry-safe; the worker should confirm `TailscaleMonitor`/`DcalHealthMonitor`/`DmailHealthMonitor` share the `pollTask == nil` guard and normalise if not, so `start()` is honestly idempotent. This is belt-and-suspenders once D1 lands.

### D3: Activation policy and `close ≠ quit`

The `AppDelegate` sets:
- `NSApp.setActivationPolicy(.regular)` — Dock icon + Cmd-Tab (the discoverability fix).
- `applicationShouldTerminateAfterLastWindowClosed(_:) -> false` — closing the dashboard hides it; the Gateway and supervised servers keep running.
- `applicationWillTerminate` — calls `controller.shutdown()` (the existing teardown path), so only a real Quit tears down children.

Reopening: a SwiftUI `WindowGroup` recreates its window when the Dock icon is clicked after the last window closed; the recreated view re-binds to the same `controller` `@StateObject` — no re-bootstrap, no leak. (If needed, `applicationShouldHandleReopen` makes the window re-show explicit.)

- **Why `.regular` (Dock) is forced by the choice of a window:** an accessory app with a closed window is unreachable. "I want a window" ⇒ Dock icon ⇒ regular activation ⇒ `close ≠ quit` — they come as a package.

### D4: `WindowGroup`-primary dashboard with an optional `MenuBarExtra`

Scene set becomes:
- `WindowGroup { DashboardView().environmentObject(controller) }` — the primary surface.
- `MenuBarExtra(isInserted: $showTrayIcon) { GlanceView().environmentObject(controller) } label: { … rollup-tinted icon … }` — `showTrayIcon` is a persisted setting (`@AppStorage`), default **false**. `GlanceView` reuses the existing `MenuBarView` content.
- The separate `Settings` scene is **removed**; settings becomes a section in the dashboard (D5).

`DashboardView` is a `NavigationSplitView` with a sidebar of sections — **Status**, **Services**, **Settings** — leaving a natural slot for a future Scheduler/Logs section without another redesign.

- **Alternative — single scrolling dashboard:** rejected; `NavigationSplitView` is the native macOS idiom and scales to later sections.

### D5: Settings as an in-window section, not a separate scene

The current `SettingsView` (Keychain-backed secrets, `~/.dmon/config.yaml` writes, Gateway-restart-on-save) moves verbatim into a dashboard **Settings** section. ⌘, is rebound to select that section. The persistence/secret semantics are unchanged — only the host surface moves. This avoids maintaining two settings UIs.

### D6: Dock-icon rollup tint + per-component last-poll time

- **Dock tint:** the same `HealthRegistry.rollupColor` that tinted the menu-bar `brain` image now also drives the Dock icon (a tinted/badged app icon) and the window header, so the at-a-glance health signal survives the loss of the menu-bar icon.
- **Last-poll time:** `ComponentHealth` gains `lastUpdated: Date?`. Each publisher (`GatewayManager`, the monitors, the endpoint probes) stamps it when it publishes a snapshot. The dashboard grid renders it per row (e.g. relative "12s ago"). This is additive — the rollup rule and existing `name/status/detail` fields are unchanged, so existing rollup tests stay valid.

### D7: ADR-028 framing amendment, no superseding ADR

ADR-028 calls dmonium an "always-on, **no-dock** host" in its *rationale*, but none of its numbered decisions (D1 bucket placement, D2 `dmonium`→`daemon/`, D3 `services/`, D4 dcal rename, D5 Swift-in-repo, D6 release matrix) mandates menu-bar-only or no-dock. Going window-primary therefore **contradicts no binding decision**; it relaxes descriptive prose. The change adds a one-line amendment note to ADR-028 ("dmonium is window-primary with a Dock icon and an optional menu-bar glance; the earlier 'no-dock host' framing is superseded by this") and **does not** require a superseding ADR. The substantive contract change lives where it belongs: the `daemon-host` spec deltas.

- **Escalation path (per CLAUDE.md):** if the architect/reviewer judges that the "no-dock host" framing was load-bearing enough to require a superseding ADR, that is a stop-and-ask — the resolution is to write `ADR-033` and get it accepted before applying, not to improvise.

## Risks / Trade-offs

- **[Window reopen re-runs startup → double-launch / subscription leak]** → D1/D2: startup is once-only in `didFinishLaunching`, guarded by `hasBootstrapped`; registration runs exactly once.
- **[Closing the window kills the daemon]** → D3: `applicationShouldTerminateAfterLastWindowClosed → false`; only Quit calls `shutdown()`.
- **[Login-item headless launch never starts children]** → D1: bootstrap is window-independent; children start at `didFinishLaunching` regardless of window visibility.
- **[Loss of the menu-bar at-a-glance signal]** → D6: Dock-icon tint carries the rollup colour; the menu-bar icon remains available as an opt-in.
- **[Two settings UIs drift]** → D5: the `Settings` scene is removed; settings has exactly one home.
- **[ADR discipline — relaxing accepted-ADR prose]** → D7: framing-only amendment; no numbered decision changes; explicit stop-and-ask path if judged otherwise.
- **[Regression in supervision/health behaviour during the refactor]** → the managers and `HealthRegistry` are reused as-is; `DaemonController` only relocates *where* their lifecycle is driven. Existing tests plus new bootstrap/idempotence tests guard it.

## Migration Plan

`daemon/Daemon.App`-local and additive in spirit. No data migration (config/Keychain layout unchanged). Rollout = ship the windowed `DaemonApp` + `DaemonController`; the optional tray defaults off so the visible change is "a window now opens with a Dock icon." Rollback = revert `daemon/Daemon.App` (no other component depends on its UI shape). Build/test seam (`make daemon-app`, `DaemonAppTests`) unchanged.

## Open Questions

- **Dock-icon tinting mechanics.** Whether to tint via `NSApp.applicationIconImage` recolouring, a badge overlay, or a template-image swap is an implementation detail for the worker; the requirement (Dock reflects the rollup colour) is fixed, the mechanism is not.
- **`@AppStorage` vs config.yaml for the "show tray" toggle.** Leaning `@AppStorage` (pure UI preference, not a daemon setting), but if it should round-trip through `~/.dmon/config.yaml` like other settings, confirm at apply time.
- **Future Status/Logs section.** Out of scope here; noted so the `NavigationSplitView` sidebar is structured to admit it later.
