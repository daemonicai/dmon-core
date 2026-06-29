# DEVLOG — mlx-local-runtime

Cross-block memory for the architect (spawned fresh each block). Newest decisions first per group.

## Status snapshot

- **Group 1 (ADR-034 gate): DONE.** ADR-034 written and **Accepted** by the user (2026-06-29). Implementation is unblocked.
- **Group 2 (core `ISessionActivityListener` seam): DONE.** Reviewer-approved. See Group 2 section below.
- Next: Group 3 (`Dmon.Providers.Mlx` — environment & lifecycle).

## Pinned facts (apply across all blocks)

- **Model pairing (spike-verified, do not change):** first-line = `mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit`; escalation = `mlx-community/gemma-4-26B-A4B-it-qat-nvfp4`. **nvfp4 is disallowed as the first-line default** (over-quantizes the small E4B → unusable tool calling).
- **No custom Python server.** Stock `mlx_lm.server` ≥ 0.31.3 does gemma-4 tool calls end-to-end via its `gemma4` parser. Version pin is load-bearing (issue #1096 parser fix).
- **`uv` is the runtime prerequisite.** It owns a pinned interpreter and pins `mlx_lm`; system Python (homebrew 3.14) cannot import `mlx_lm`. `IsApplicable()` = arm64/macOS + `uv` on PATH (cheap, no I/O); env-build + spawn happen in `EnsureRunningAsync()`.
- **Readiness probe ≠ `/v1/models`** (lists *cached*, not *resident*, models). Use a tiny completion or `/health` (verify `/health` reflects load state during impl).
- **gemma-4 emits a separate `reasoning` field**; `max_tokens` must be generous or the tool call is never reached.
- **Escalation runtime uses a FIXED port** so ADR-032's cached escalation `IChatClient` reconnects after teardown→respawn.
- **Test gate:** run `env -u MEKO_API_KEY make test` (avoids the live-Meko smoke hang). `make build` is `TreatWarningsAsErrors`.

## Group 2 — core `ISessionActivityListener` seam (2.1–2.4, DONE)

- **Interface home:** `core/Dmon.Abstractions/Hosting/ISessionActivityListener.cs`, namespace `Dmon.Abstractions.Hosting` (next to `ITerminalClientFactory`). Both `Dmon.Core` and `daemon/Daemon.Routing` reference Abstractions, so the Group-6 daemon listener can depend on it.
- **Shape:** synchronous `void OnSessionActivated(string sessionId)` / `void OnTurnStarted(string sessionId)` — no `cancellationToken`, no async. The core caller never awaits listener work; the Group-6 consumer does its own fire-and-forget internally.
- **Wiring is live with zero new registration:** `SessionHandler`/`TurnHandler` are registered via the activator (`AddSingleton<T>()` at `DaemonServiceExtensions.cs:130,136`), so MS DI materialises the `IEnumerable<ISessionActivityListener>` ctor param itself — empty when none registered, full set otherwise. The `= null` ctor default (normalised by `?? []`) exists only for direct-`new` test paths. **Group 6 just registers an `ISessionActivityListener` impl; no handler-wiring change needed.**
- **Firing points:** `OnSessionActivated(meta.Id)` after `_currentSession = meta` in both `CreateAsync` and `LoadAsync`; NOT on the locked-session early return. `OnTurnStarted` after the turn-gate is acquired, inside the `try`; NOT on the turn-in-progress / no-active-session early returns (skips when `CurrentSession?.Id` is null). Per-listener try/catch (each call isolated, not one try around the loop), logged + swallowed.
- **In-process only:** no `IEventEmitter` emission, no protocol DTO, no `schema.json` change (honours ADR-003/016). Reviewer verified the diff is exactly 4 files.
- **Architectural note (for Group 6):** Fork/Clone deliberately do NOT fire `OnSessionActivated` (they leave `_currentSession` unchanged; spec is scoped to create/load). If the warming service ever needs a forked/cloned session treated as "active," that semantic doesn't exist today and would need its own spec delta — don't assume it silently.

## Group 1 — ADR-034 (gate)

- **1.1 / 1.2 (DONE).** Wrote `docs/adrs/ADR-034-mlx-local-runtime.md`, accepted by user → Status: Accepted.
  - **Decision 1 (amends ADR-007):** `StopAsync(CancellationToken)` added to `IProviderExtension` with a **default no-op** → existing providers source-compatible, no edits. A provider that owns a spawned server kills it + releases its port on `StopAsync`. (Backs tasks 5.1 and 3.5.)
  - **Decision 2 (amends ADR-006):** composition-declared backends carry **standing spawn consent** — no per-call confirmation prompt for start/warm/respawn. Replaces ADR-007 D2's `tool.confirmRequest risk:high` gate **for composition-declared backends only**; interactive/ad-hoc use keeps the gate. (Enables `EscalationWarmingService` to warm/teardown repeatedly without prompting.)
  - **Decision 3 (amends ADR-032 D3):** escalation backend = fixed-port mlx runtime + activity-warming + idle-teardown. Warming is **additive/best-effort**; the escalation path's lazy `EnsureRunningAsync` is **retained** as the self-heal backstop. Three-layer split: **core** = neutral `ISessionActivityListener` (`OnSessionActivated`/`OnTurnStarted`, in-process, NOT on the RPC wire) fired by `SessionHandler` + `TurnHandler`; **daemon** = `EscalationWarmingService` + idle timer (routing policy home per ADR-027 D5); **provider** = `EnsureRunningAsync`/`StopAsync` mechanism, knows nothing of sessions.
  - Reviewer signed off; applied 5 polish nits (model-label wording, Amends header wording "provider-spawn confirmation gate", ADR-007 relationship bullet now notes the gate narrowing, added ADR-021/ADR-024 to Builds-on + relationship bullets, softened the recompile claim to source-compatible-via-default-method).
  - Idle-timeout exact default left as Open Question (lean ~10 min, configurable). Readiness `/health` vs completion and `reasoning`-field mapping are Open Questions to resolve during impl.
