# Daemon.App (`dmonium`)

The `dmonium` macOS app for the DAEMON personal-assistant surface. It is
**window-primary**: a `WindowGroup` dashboard (Status / Services / Settings) with
a Dock icon and Cmd-Tab presence is the main surface; a menu-bar icon remains
available as an **optional, default-off** glance affordance (toggle it in
Settings). Closing the dashboard window does **not** quit the app — supervision
keeps running and the window reopens from the Dock; only Quit tears down the
supervised children.

This is a **Swift Package Manager** package, **not** an Xcode project — there is
no `.xcodeproj`/`.xcworkspace` checked in. It is built outside `Everything.slnx`
(which is .NET-only) per [ADR-028](../../docs/adrs/ADR-028-personal-assistant-monorepo-topology.md).

## Open in Xcode

Xcode opens SwiftPM packages natively (it resolves `Package.swift` into an
in-memory project with a runnable `DaemonApp` scheme):

```sh
xed daemon/Daemon.App
# or: open daemon/Daemon.App/Package.swift
# or: Xcode → File → Open… → select the Daemon.App folder
```

## Build / run from the command line

From the repo root, the wrapper target is:

```sh
make daemon-app
```

which runs:

```sh
swift build -c release --package-path daemon/Daemon.App
```

The executable lands at `daemon/Daemon.App/.build/release/DaemonApp`.

## Test

```sh
swift test --package-path daemon/Daemon.App
```

The `DaemonAppTests` target unit-tests the pure logic seams (process-adoption
liveness via an injected probe, health-status classification and the aggregate
rollup, and the `ConfigStore` flat-YAML round-trip). Tests spawn no processes
and make no network calls.

## What the app does

`dmonium` supervises the DAEMON stack from a window-primary dashboard:

- **Process supervision** — launches/adopts and restarts (exponential back-off)
  the **Network** host (which builds+runs the `Daemon.cs` core), and the **Dcal** and
  **Dmail** servers. The network host is installed via `make network` / `dotnet tool install`
  (command `ndmon`), resolves at `~/.dotnet/tools/ndmon`, overridable with
  `DMON_NETWORK_PATH`. Other server binaries resolve from `DMON_DCAL_SERVER_PATH` /
  `DMON_DMAIL_SERVER_PATH`; an unresolved server is reported "not configured" rather
  than spawned. All children are terminated on quit.
- **Unified health** — a typed `HealthRegistry` aggregates the Network host, the
  Dcal/Dmail servers, Tailscale, the calendar-sync poll, and the configured
  model-runner endpoints (`DMON_E2B_URL`, `DMON_REASONER_URL`, egress). Each is a
  row in the dashboard's Status grid (with its last-poll time); the **Dock icon**
  (and the optional menu-bar icon, when enabled) reflects the rollup
  (red/amber/green). The Services section offers per-service start/stop/restart and
  a best-effort "Bring Tailscale up" action (`tailscale up`).
- **Settings** — writes `~/.dmon/config.yaml` (+ Keychain for secrets) and
  restarts the Network host. dmon-core's own keys use the `DMON_` prefix (endpoints,
  the three `DMON_*_MODEL` IDs, `DMON_EGRESS_THRESHOLD`); the Dcal/Dmail servers
  keep their own `DCAL_*`/`DMAIL_*` config and provider keys (`GEMINI_API_KEY`)
  are unchanged.

## Layout

- `Package.swift` — manifest; `DaemonApp` executable target + `DaemonAppTests`, macOS 14+.
- `Sources/DaemonApp/` — app sources:
  - UI/lifecycle: `DaemonApp.swift` (scene set: `WindowGroup` + optional `MenuBarExtra`), `DaemonController.swift` (the single `@MainActor` owner of the managers + `HealthRegistry`, bootstrapped once from `applicationDidFinishLaunching`), `DashboardView.swift` (the `NavigationSplitView` dashboard), `StatusHelpers.swift` (status/rollup presentation helpers), `MenuBarView.swift`, `SettingsView.swift`, `LoginItemManager.swift`, `Keychain.swift`.
  - Process supervision: `ServerProcessManager.swift` (reusable), `NetworkManager.swift`, `ServiceManager.swift` (Dcal/Dmail).
  - Health: `ComponentHealth.swift`, `HealthRegistry.swift`, `TailscaleMonitor.swift`, `DcalHealthMonitor.swift`, `DmailHealthMonitor.swift`, `EndpointHealthProbe.swift`.
- `Tests/DaemonAppTests/` — `ServerProcessManagerTests`, `HealthClassificationTests`, `ConfigStoreTests`, `DaemonControllerTests` (bootstrap idempotence), `RelativeAgeTests`, `MenuBarPreferenceTests`, `PublisherLastUpdatedTests`.
