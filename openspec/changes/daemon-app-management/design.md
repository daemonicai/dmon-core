## Context

`daemon/Daemon.App` (`dmonium`, ADR-028) is a SwiftPM menu-bar app. Today it manages only the Gateway process (`GatewayManager`: launch, PID-file adopt, exponential back-off, env injection) and shows three status rows (Gateway, Tailscale, Dcal last-sync). The Dcal and Dmail **servers** (standalone artifacts under `services/`, paired with `tools/` extensions per ADR-028) are not supervised by the app; Dmail has no health probe; local model runners have none. Inference settings in `Daemon.cs` are mis-prefixed `DCAL_*` and the three model IDs are hardcoded literals, so model changes require source edits.

The Gateway already owns build+run of the core: `core/Dmon.Runtime` `CoreLauncher`/`CoreProcessManager` runs `dotnet build <Daemon.cs>` (incremental) then `dotnet run --no-build`. `DmonHostBuilder` exposes `IConfiguration` layering env vars (via `Host.CreateApplicationBuilder`) under `~/.dmon/config.yaml`. So config-only settings need no new core machinery — only that `Daemon.cs` reads every knob from `builder.Configuration`.

## Goals / Non-Goals

**Goals:**
- Supervise Gateway, Dcal server, and Dmail server from one reusable process manager.
- One typed health surface aggregating all components (incl. Dmail and the configured model-runner endpoints), driving menu rows and the icon.
- Make every `Daemon.cs` knob — including the three model IDs — config-driven via `IConfiguration`; standardize dmon-core's own env vars on the `DMON_` prefix.
- A `DaemonAppTests` XCTest target so app logic has a real gate.

**Non-Goals:**
- A gateway core-restart wire command (deferred — settings-apply keeps bouncing the whole Gateway process, which restarts the core).
- Any `Daemon.cs` source-editing capability / Roslyn helper.
- Renaming `DCAL_*` / `DMAIL_*` **server** vars or provider keys (`GEMINI_API_KEY`); deep Tailscale auth/login flows.
- Any change to `core/`, `frontends/`, the wire protocol, or any ADR.

## Decisions

**1. One configurable `ServerProcessManager`, not a class per server.** Extract the Gateway's launch / adopt(PID) / back-off / intentional-stop / terminate-on-quit logic into a reusable `ServerProcessManager` configured with: display name, executable-resolution candidate list, launch arguments, PID-file path, and an env dictionary. `GatewayManager` becomes that manager configured with `--agent daemon/Daemon.cs`; Dcal and Dmail get their own instances. *Alternative — a Swift protocol with three concrete classes:* rejected; the lifecycle is identical, only data differs, so configuration beats subclassing.

**2. Server binary resolution is config-first with a "not configured" terminal state.** Each server resolves its executable as: override key (`DMON_DCAL_SERVER_PATH` / `DMON_DMAIL_SERVER_PATH`, from config.yaml or env) → conventional default candidate(s). If nothing resolves to an executable, the server is reported **`unknown` ("not configured")**, NOT an error, and is not spawned. This avoids guessing each service's install layout and keeps a missing server visually distinct from a crashed one. *Alternative — bake a hard default path and fail loudly:* rejected; install layout for the services is not yet a settled convention (see Open Questions).

**3. Typed health via a `HealthRegistry`.** Introduce `ComponentHealth { name: String, status: HealthStatus, detail: String? }` with `HealthStatus = ok | degraded | down | unknown`. Each monitor publishes its own `ComponentHealth`; `HealthRegistry: ObservableObject` collects them into an ordered `[ComponentHealth]` plus an overall rollup. `MenuBarView` renders one row per component. **Icon rollup** (extends the current table): Gateway stopped → red; else any component `down` → red; else any `degraded`/`unknown` → amber; else green. The existing red/amber/green requirement is preserved and generalized.

**4. Model-runner health = generic endpoint reachability, not per-runner logic.** A reusable `EndpointHealthProbe` issues a lightweight HTTP request to each configured inference endpoint (`DMON_E2B_URL`, `DMON_REASONER_URL`, and the egress/Gemini target) and classifies reachable→`ok` / unreachable→`down`. It does NOT enumerate models or special-case Ollama/Omlx/MTPLX/Llama.cpp — any HTTP response counts as reachable. This stays correct as runners change and avoids coupling the app to runner-specific APIs.

**5. `Daemon.cs` builder-first reorder, all knobs via `IConfiguration`.** Create `DmonHostBuilder` first, then read `builder.Configuration.GetValue<string>(key, default)` for endpoints, the Gemini key, and the three model IDs, before constructing the `IChatClient`s. New keys: `DMON_E2B_MODEL` / `DMON_REASONER_MODEL` / `DMON_EGRESS_MODEL`. Rename `DCAL_E2B_URL`→`DMON_E2B_URL`, `DCAL_REASONER_URL`→`DMON_REASONER_URL` (and `DAEMON_EGRESS_THRESHOLD`→`DMON_EGRESS_THRESHOLD` in the app, which is app/config-only and not yet read by the router). `GetValue<T>` comes from `Microsoft.Extensions.Configuration.Binder` (transitive via `Dmon.Core`; confirm at build).

**6. Testability seams.** Keep side-effecting calls (process spawn, `kill(2)`, HTTP, `tailscale` exec) behind small injectable functions so `DaemonAppTests` can unit-test the pure logic: PID liveness from an injected probe, Tailscale/endpoint status classification from decoded inputs, `ConfigStore` flat-YAML round-trip, and settings env-key mapping. No test spawns a real process or hits the network.

## Risks / Trade-offs

- **Swift sits outside `Everything.slnx` / `make test`.** → Gate Swift with `make daemon-app` (build) and `swift test` (the new XCTest target); the C# `make build`/`make test` gates still cover `Daemon.cs`.
- **Dcal/Dmail server install path convention is unconfirmed.** → Config-overridable path + "not configured" (`unknown`) state means the app degrades gracefully and never spawns the wrong binary; the architect confirms the default candidate during apply.
- **Settings-apply bounces the whole Gateway, dropping live connections.** → Accepted this round; the targeted core-restart command is explicitly deferred and tracked as future work.
- **`DMON_` rename is a clean break** (pre-release, no migration per project policy). → A stale `~/.dmon/config.yaml` with old `DCAL_E2B_URL` keys silently falls back to defaults; acceptable. Settings panel writes the new keys on next save.
- **Running `tailscale up` may require elevated auth.** → Best-effort menu action only; surfaces failure in the Tailscale row, no embedded login flow.

## Open Questions

- **Default install location for the Dcal/Dmail server executables** (and whether they launch as a native exe vs `dotnet <dll>`). Resolved during apply by the architect inspecting `services/Dcal` / `services/Dmail` build output; until then the manager relies on the override key and reports `unknown` when unresolved.
