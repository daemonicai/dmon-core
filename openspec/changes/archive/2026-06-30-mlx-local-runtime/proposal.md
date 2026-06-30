## Why

The Daemon (ADR-032) needs two local models resident at once — `gemma-4-e4b-it-qat-OptiQ-4bit` (first-line, kept hot for instant session start) and `gemma-4-26B-A4B-it-qat-nvfp4` (the `think_harder` escalation model, warmed early so the e4b→26b hand-off is smooth). The current `Dmon.Providers.Omlx` launches the oMLX **GUI app** (`open -a oMLX`) which holds only **one** model in memory at a time, so it cannot satisfy this. A headless, scriptable runtime that runs **one model per process on its own port** is required. A spike (captured in `docs/notes/mlx-runtime-exploration.md`) confirmed stock `mlx_lm.server` does gemma-4 tool calls correctly end-to-end (no custom Python server needed), and that the small E4B must use the OptiQ-4bit quant (nvfp4 over-quantizes it into unusable tool-calling) while the 26B tolerates nvfp4.

## What Changes

- **NEW `Dmon.Providers.Mlx`** — a headless provider that spawns stock `mlx_lm.server` (no custom Python) via a **uv-managed virtual environment**. `uv` becomes a runtime prerequisite: it owns the pinned Python interpreter and pins `mlx_lm >=` the gemma4-tool-parser version (0.31.3). `IsApplicable()` is a cheap check (arm64/macOS + `uv` on PATH); all env-build + spawn work happens in the gated `EnsureRunningAsync()`.
- The provider exposes **two keyed runtimes**: `"firstline"` (E4B-OptiQ-4bit, fixed port, permanent) and `"escalation"` (26B-A4B-nvfp4, separate **fixed** port, idle-managed). Each runtime is one `mlx_lm.server` process owned by its real PID (matching the Mtplx/LlamaCpp `_serverProcess` pattern).
- **NEW provider lifecycle verb `StopAsync()`** to complement attach-first `EnsureRunningAsync()` (today the local providers are start-only). **BREAKING** to the provider lifecycle contract (ADR-007).
- **NEW core seam `ISessionActivityListener`** — an in-process, DI-discovered notification (`OnSessionActivated` / `OnTurnStarted`) fired by `SessionHandler` and `TurnHandler`. Carries no policy; it is a neutral activity signal.
- **NEW daemon `EscalationWarmingService`** — implements `ISessionActivityListener` plus an idle timer. On activity it fires a best-effort, fire-and-forget `EnsureRunningAsync` on the escalation runtime and resets the timer; after N minutes idle it calls `StopAsync`. Warming is a latency optimization only — the escalation path already ensures-running, so it self-heals if warming missed.
- The readiness probe becomes a **tiny completion (or `/health`)**, not `/v1/models` (which lists *cached*, not *resident*, models). gemma-4's separate `reasoning` field and generous `max_tokens` are accounted for.
- **REMOVE `Dmon.Providers.Omlx`** and switch the Daemon composition from `UseOmlx` to the new mlx verbs. **BREAKING** (removes the `UseOmlx` verb). Clean break — no production deployments to migrate.
- **NEW ADR-034** amending ADR-006 (composition-declared backends carry standing spawn consent — cannot prompt on every warm/respawn), ADR-007 (add `StopAsync` to the provider lifecycle), and ADR-032 (escalation backend = mlx escalation runtime on a fixed port, with activity-warming + idle-teardown). **Must be accepted before implementation.**

Out of scope: model-download orchestration (swift-transformers vs Python `hf download`) — rely on `mlx_lm`'s own first-run download for now. The `daemon-scheduler` change is explicitly not folded in. The shared local-runtime lifecycle helper (the third near-clone of spawn/poll/probe) is deferred.

## Capabilities

### New Capabilities
- `mlx-provider`: the `Dmon.Providers.Mlx` headless local runtime — uv-managed env, version-pinned `mlx_lm.server`, two keyed runtimes (permanent first-line / fixed-port escalation), attach-first `EnsureRunningAsync` + `StopAsync`, completion/`/health` readiness probe, tool-calling probe, reasoning-field + generous `max_tokens` handling, and the mlx composition verbs.
- `session-activity`: the core in-process `ISessionActivityListener` seam (`OnSessionActivated`/`OnTurnStarted`), DI-discovered, fired by `SessionHandler` and `TurnHandler`.
- `escalation-warming`: the daemon `EscalationWarmingService` — consumes `session-activity`, performs fire-and-forget warming and idle-timeout teardown of the escalation runtime via the provider's `EnsureRunningAsync`/`StopAsync`.

### Modified Capabilities
- `provider-extension`: the provider lifecycle gains `StopAsync()` (start-only → start/stop). (ADR-007)
- `triage-routing`: the escalation backend is the mlx escalation runtime on a fixed port; warming is integrated as a best-effort optimization with a self-heal guarantee on the escalation path. (ADR-032)
- `permission-model`: composition-declared backends carry standing consent to (re)spawn — no per-warm/respawn prompt; interactive provider use keeps the prompt-gate. (ADR-006)
- `omlx-provider`: **REMOVED** — superseded by `mlx-provider`. The `UseOmlx` verb and the oMLX GUI-app launcher are deleted.

## Impact

- **New packages/code:** `providers/Dmon.Providers.Mlx`; core `ISessionActivityListener` + invocation in `SessionHandler`/`TurnHandler`; daemon `EscalationWarmingService`; `daemon/Daemon.cs` composition switched to the mlx verbs.
- **Removed:** `providers/Dmon.Providers.Omlx` (package + `omlx-provider` spec + `UseOmlx`).
- **New runtime prerequisite:** `uv` on PATH (owns the interpreter + pins `mlx_lm`). No system-Python dependency.
- **Models on disk:** `mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit` and `mlx-community/gemma-4-26B-A4B-it-qat-nvfp4` (downloaded by `mlx_lm` on first run for now).
- **ADRs:** ADR-034 (amends ADR-006/007/032) — must be accepted first.
- **Docs:** the daemon deploy/setup docs gain the `uv` prerequisite and the mlx model pairing.
