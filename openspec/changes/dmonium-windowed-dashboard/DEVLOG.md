# DEVLOG — dmonium-windowed-dashboard

> **Summary (pinned):** Converting `dmonium` (`daemon/Daemon.App`, a Swift Package) from menu-bar-only into a window-primary dashboard with an optional default-off menu-bar icon. Architecture is design.md D1–D7; the lifecycle crux is D1/D2 (a single `DaemonController` owner bootstrapped once from `applicationDidFinishLaunching`, never a view `.task`). Gates are **Swift, not .NET**: `swift build -c release --package-path daemon/Daemon.App`, `swift test --package-path daemon/Daemon.App`, `openspec validate dmonium-windowed-dashboard --strict`. Branch `change/dmonium-windowed-dashboard`.

## Group 1 — DaemonController owner + window-independent bootstrap

### Block 1.1–1.2 — `ComponentHealth.lastUpdated` + stamping (DONE)

- **What:** Added `lastUpdated: Date?` (default `nil`) to `ComponentHealth` and stamped it at all six publish sites. Data-model + stamping half of **D6**; the *rendering* ("12s ago" in the dashboard grid) is deferred to task **3.3**.
- **Two publish shapes (worker/architect note):** publishers construct `ComponentHealth` two ways — (a) declarative Combine `.map` in `init` (`GatewayManager`, `ServiceManager`, `TailscaleMonitor`) where `Date()` must be stamped *inside* the closure so it evaluates per-emission, and (b) imperative assignment in poll/result handlers (`DcalHealthMonitor`, `DmailHealthMonitor`, `EndpointHealthProbe`). Both handled.
- **Seed values stay `nil`:** the initial `.unknown`/`.down` seeds are deliberately left unstamped — `lastUpdated == nil` is the "never published yet" semantic. This also keeps the existing rollup/classifier tests valid (the `component(_:)` test helper omits `lastUpdated` → `nil` on both sides of equality).
- **Rollup untouched:** `lastUpdated` does NOT enter `HealthRegistry.rollup` (still `name/status`-only); `HealthRegistry.swift` unchanged.
- **Tests:** +3 deterministic tests in `HealthClassificationTests.swift` (seed isNil; stamped isNonNil; two stamps non-decreasing via `<=`, no flake). Suite 31 → 34, all green.
- **Gates:** release build clean (0 warnings); `swift test` 34/0; `openspec validate --strict` valid. Reviewer: **Approve**, no blockers/nits.
- **Carry-forward:** spec "Unified health surface" also requires the dashboard to *render* the timestamp — closed later by **3.3** (Group 3). Reviewer flagged this as correctly out-of-scope here.
