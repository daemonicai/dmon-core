# DEVLOG — mlx-local-runtime

Cross-block memory for the architect (spawned fresh each block). Newest decisions first per group.

## Status snapshot

- **Group 1 (ADR-034 gate): DONE.** ADR-034 written and **Accepted** by the user (2026-06-29). Implementation is unblocked.
- **Group 2 (core `ISessionActivityListener` seam): DONE.** Reviewer-approved. See Group 2 section below.
- **Group 3 partial (3.1–3.2 scaffold + applicability): DONE.** Reviewer-approved. See Group 3 section below.
- **Group 3 partial (3.3/3.4/3.6/3.7 `EnsureRunningAsync` end-to-end): DONE.** Reviewer-approved. See Group 3 (EnsureRunningAsync) section below.
- Next: a block pairing **5.1 + 3.5 (+ 5.2)** — add `StopAsync` default no-op to `IProviderExtension` (5.1), implement the provider's `StopAsync` (3.5), tests (5.2 + remaining 3.8). The `Dispose`/`KillServer`/`OwnsProcess` plumbing is already in place for 3.5 to reuse.

## Pinned facts (apply across all blocks)

- **Model pairing (spike-verified, do not change):** first-line = `mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit`; escalation = `mlx-community/gemma-4-26B-A4B-it-qat-nvfp4`. **nvfp4 is disallowed as the first-line default** (over-quantizes the small E4B → unusable tool calling).
- **No custom Python server.** Stock `mlx_lm.server` ≥ 0.31.3 does gemma-4 tool calls end-to-end via its `gemma4` parser. Version pin is load-bearing (issue #1096 parser fix).
- **`uv` is the runtime prerequisite.** It owns a pinned interpreter and pins `mlx_lm`; system Python (homebrew 3.14) cannot import `mlx_lm`. `IsApplicable()` = arm64/macOS + `uv` on PATH (cheap, no I/O); env-build + spawn happen in `EnsureRunningAsync()`.
- **Readiness probe ≠ `/v1/models`** (lists *cached*, not *resident*, models). Use a tiny completion or `/health` (verify `/health` reflects load state during impl).
- **gemma-4 emits a separate `reasoning` field**; `max_tokens` must be generous or the tool call is never reached.
- **Escalation runtime uses a FIXED port** so ADR-032's cached escalation `IChatClient` reconnects after teardown→respawn.
- **Test gate:** run `env -u MEKO_API_KEY make test` (avoids the live-Meko smoke hang). `make build` is `TreatWarningsAsErrors`.

## Group 3 — `Dmon.Providers.Mlx` EnsureRunningAsync (3.3/3.4/3.6/3.7, DONE)

- **Flow:** `EnsureRunningAsync` = provision venv → version-pin check → attach-first/spawn → poll readiness → one-time tool probe. Implemented in `MlxProviderExtension.cs`, replacing the 3.x throwing stubs. `CreateFactory()` STILL throws — message now points at **task 4.1** (production client/factory).
- **Version pin:** `IsVersionBelowPin` uses `System.Version` (numeric compare, not string — `0.31.10 > 0.31.3`, `0.4.0 < 0.31.3` rank correctly); unparseable → fail-fast throw. **Pin check runs BEFORE any spawn** (tested: no spawn on pin failure). Pin = `mlx_lm >= 0.31.3`.
- **Provisioning seam:** `_provisionEnvDelegate : Func<CancellationToken, Task<string>>` returns the resolved `mlx_lm` version (prod impl runs `uv venv` + `uv pip install` + resolves version; tests inject version strings). Venv shared across runtimes; idempotent.
- **Spawn:** `BuildServerArguments(port)` → `-m mlx_lm.server --model <id> --port <_options.Port> --host <host>`; fixed port, no dynamic assignment; asserted without spawning. Real process handle retained.
- **Attach-first:** `OwnsProcess=false` on attach (healthy server already on port, no spawn), `true` on spawn; idempotent repeat = probe-only no-op. Both tested.
- **Readiness (`IsRunningAsync` / `CheckRunningViaCompletionAsync`):** minimal completion against `_options.ModelId`, `MaxOutputTokens=16`, success = no exception (tolerant of EMPTY content for gemma-4 reasoning). **NEVER `/v1/models`** (tested). Empty BaseUrl → no network attempt.
- **Tool probe (`RunToolCallingProbeAsync`):** runs ONCE after readiness, `MaxOutputTokens=2048` (generous so gemma-4 reaches the tool call — else false-negative), records `ToolCallingVerified` on `MlxRuntimeState`.
- **Teardown:** spawn/poll/probe wrapped in try/catch → `KillServer()` (`Interlocked.Exchange` + `Kill(entireProcessTree:true)`) before rethrow; `Dispose`/`DisposeAsync` kill ONLY when `OwnsProcess`. Tested via `SetServerProcess(dummy)` with a harmless `/bin/sleep 30` (`ping` on Windows), `[SkippableFact]`-gated (`xunit.skippablefact`).
- **`MlxRuntimeState`** (dmon-owned, BCL-only): `BaseUrl`, `OwnsProcess`, `bool? ToolCallingVerified`. NO `ActiveModelId` (model id always explicit). M.E.AI/OpenAI types stay internal (csproj now refs `Microsoft.Extensions.AI.OpenAI`).
- **Open Questions resolved:** readiness = completion (NOT `/health` — its load-state semantics unverified); full `reasoning`-field mapping deferred to 4.1 (OpenAI SDK ignores the unknown field so tool_calls/content still parse).
- 40 Mlx tests pass; NO real `mlx_lm.server`/uv/network in tests (all via seams; the dispose `sleep` dummy is the only real process and it's harmless + cleaned up).

## Group 3 — `Dmon.Providers.Mlx` (3.1–3.2 scaffold, DONE)

- **Package:** `providers/Dmon.Providers.Mlx` (csproj mirrors `Dmon.Providers.Mtplx` — net10.0, TreatWarningsAsErrors, IsPackable=true, CPM no-Version, `InternalsVisibleTo Dmon.Providers.Mlx.Tests`, packaged README). Added to BOTH `Everything.slnx` and `providers.slnx`. Test project `test/Dmon.Providers.Mlx.Tests` (IsPackable=false).
- **`MlxRuntimeOptions`** (BCL-only record): `Host` (default `127.0.0.1`), `Port`, `ModelId`, `ReadyTimeout`, `IdleWindow`. Static factories `Firstline()` (port **8800**, default `mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit`, **nvfp4 guard throws `ArgumentException`**, case-insensitive) and `Escalation()` (port **8810**, default `mlx-community/gemma-4-26B-A4B-it-qat-nvfp4`, no guard). All overridable.
- **`MlxProviderExtension`**: `ProviderName => "mlx"`. `IsApplicable()` = macOS → arm64 → uv-on-PATH, each with its own remediation message (uv message names `uv` + install cmd). ZERO process/network I/O (only `OperatingSystem.IsMacOS`, `RuntimeInformation.OSArchitecture`, PATH `File.Exists` via `FindUvOnPath`). Internal test-seam ctor exposes `isMacOsOverride`/`osArchitectureOverride`/`resolveUvPathOverride`.
- **Stubs (filled by later blocks):** `IsRunningAsync`/`EnsureRunningAsync`/`ListModelsAsync`/`CreateFactory` throw `NotImplementedException("Implemented in mlx-local-runtime tasks 3.3–3.7.")`. `IProviderExtension` UNCHANGED — `StopAsync` NOT added (that's 5.1). The class is `IDisposable` (no-op `Dispose` now; later blocks close the server process there).
- **Provider is PER-RUNTIME** (one instance = one model/port/process, like Mtplx). The two-keyed-runtime registration is task 4.2's composition concern — NOT built here.
- 17 tests: full applicability matrix (incl. asserting the override seam is consulted = no real I/O) + options defaults/overrides/nvfp4-guard.
- **For task 4.2 (composition verbs):** README's forward-looking `.AddMlxFirstline()`/`.AddMlxEscalation()` example was REMOVED because the verb names aren't fixed yet — 4.2 owns naming them.

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
