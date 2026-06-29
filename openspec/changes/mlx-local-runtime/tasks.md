## 1. ADR-034 (gate — accept before any code)

- [x] 1.1 Write `docs/adrs/ADR-034-mlx-local-runtime.md` amending ADR-007 (add `StopAsync` to the provider lifecycle), ADR-006 (composition-declared backends carry standing spawn consent), and ADR-032 (escalation backend = fixed-port mlx runtime with activity-warming + idle-teardown); status Proposed.
- [x] 1.2 Obtain user acceptance of ADR-034 and set status Accepted. **Stop-and-ask gate: no implementation tasks proceed until accepted.**

## 2. Core: session-activity seam

- [x] 2.1 Define `ISessionActivityListener` (`OnSessionActivated(sessionId)`, `OnTurnStarted(sessionId)`) in core and register DI discovery of zero-or-more listeners.
- [x] 2.2 Invoke `OnSessionActivated` from `SessionHandler` on create and load, with per-listener exception isolation (must not fail the session command); keep in-process (no RPC-wire event).
- [x] 2.3 Invoke `OnTurnStarted` from `TurnHandler` at turn start, with per-listener exception isolation (must not fail the turn).
- [x] 2.4 Tests: listeners discovered/invoked on activate + turn; zero registrations is a no-op; a throwing listener does not break session create/load or the turn.

## 3. Provider: Dmon.Providers.Mlx — environment & lifecycle

- [x] 3.1 Scaffold `providers/Dmon.Providers.Mlx` (project, namespace, options) with keyed-runtime config (`firstline`/`escalation`: model id, fixed port, idle window) and defaults (E4B-OptiQ-4bit / 26B-A4B-nvfp4); reject nvfp4 as the first-line default.
- [x] 3.2 Implement `IsApplicable()` = arm64/macOS + `uv` on PATH (cheap, no I/O) with a remediation message.
- [x] 3.3 Implement uv-managed venv provisioning inside `EnsureRunningAsync()` (pinned interpreter + `mlx_lm >= 0.31.3`); fail fast if resolved `mlx_lm` is below the pin; no system-Python dependency.
- [x] 3.4 Implement attach-first `EnsureRunningAsync()` spawning `<venv>/bin/python -m mlx_lm.server --model <id> --port <fixed> --host 127.0.0.1`, retaining the real process handle.
- [x] 3.5 Implement `StopAsync()` killing an owned server process and releasing the port; no-op/leave-running for attached-only runtimes.
- [x] 3.6 Implement readiness via a minimal completion (or `/health` if it reflects load state); do NOT use `/v1/models` for readiness or resident-model inference.
- [x] 3.7 Implement the one-time tool-calling capability probe after readiness (mirror Mtplx/LlamaCpp).
- [x] 3.8 Tests: applicability matrix; attach-first vs spawn; StopAsync owned vs attached; readiness probe ignores `/v1/models`; version-pin rejection.

## 4. Provider: Dmon.Providers.Mlx — client & composition

- [ ] 4.1 Implement the mlx `IChatClient` construction over a runtime's base URL with reasoning-aware defaults (generous `max_tokens`; `reasoning` field handled without corrupting `content`/`tool_calls`).
- [ ] 4.2 Implement the mlx composition verbs registering both keyed runtimes (resolvable by key for warm/stop and client construction) and wiring first-line + escalation backends; remove the need for `UseOmlx`.
- [ ] 4.3 Tests: client parses tool_calls with a reasoning field present; verbs register resolvable keyed runtimes.

## 5. Provider lifecycle contract (StopAsync) across existing providers

- [x] 5.1 Add `StopAsync` to the provider lifecycle contract with a default no-op so start-only/attach-only providers are unaffected (per ADR-034).
- [x] 5.2 Tests: default no-op leaves external processes untouched.

## 6. Daemon: EscalationWarmingService

- [ ] 6.1 Implement `EscalationWarmingService : ISessionActivityListener` in the daemon: on activate/turn → fire-and-forget `EnsureRunningAsync(escalation)` + reset idle timer; never block the caller.
- [ ] 6.2 Implement the idle timer → `StopAsync(escalation)` after the configurable idle window (sane default ~10 min); activity cancels pending teardown.
- [ ] 6.3 Register the service in `daemon/Daemon.cs`; ensure the escalation runtime uses a FIXED port so a cached client reconnects after respawn.
- [ ] 6.4 Tests: warm on activate + turn; idle teardown after window; activity cancels teardown; warming never blocks; escalation-before-warmup self-heals via the escalation path.

## 7. Daemon composition switch & Omlx removal

- [ ] 7.1 Switch `daemon/Daemon.cs` from `UseOmlx` to the mlx verbs (first-line + escalation) with the verified model pairing.
- [ ] 7.2 Remove `providers/Dmon.Providers.Omlx` (package + references + solution entries) and delete the `omlx-provider` standing spec on archive.
- [ ] 7.3 Tests: daemon composition DI graph resolves (first-line/escalation backends + warming service) with no Omlx references remaining.

## 8. Docs & validation

- [ ] 8.1 Update the daemon deploy/setup docs: `uv` prerequisite, the OptiQ-4bit/nvfp4 model pairing, first-run model download note.
- [ ] 8.2 `make build` clean (TreatWarningsAsErrors), `env -u MEKO_API_KEY make test` green, `openspec validate mlx-local-runtime --strict` passes.
