# DEVLOG — mlx-local-runtime

Cross-block memory for the architect (spawned fresh each block). Newest decisions first per group.

## Status snapshot

- **Group 1 (ADR-034 gate): DONE.** ADR-034 written and **Accepted** by the user (2026-06-29). Implementation is unblocked.
- Next: Group 2 (core `ISessionActivityListener` seam).

## Pinned facts (apply across all blocks)

- **Model pairing (spike-verified, do not change):** first-line = `mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit`; escalation = `mlx-community/gemma-4-26B-A4B-it-qat-nvfp4`. **nvfp4 is disallowed as the first-line default** (over-quantizes the small E4B → unusable tool calling).
- **No custom Python server.** Stock `mlx_lm.server` ≥ 0.31.3 does gemma-4 tool calls end-to-end via its `gemma4` parser. Version pin is load-bearing (issue #1096 parser fix).
- **`uv` is the runtime prerequisite.** It owns a pinned interpreter and pins `mlx_lm`; system Python (homebrew 3.14) cannot import `mlx_lm`. `IsApplicable()` = arm64/macOS + `uv` on PATH (cheap, no I/O); env-build + spawn happen in `EnsureRunningAsync()`.
- **Readiness probe ≠ `/v1/models`** (lists *cached*, not *resident*, models). Use a tiny completion or `/health` (verify `/health` reflects load state during impl).
- **gemma-4 emits a separate `reasoning` field**; `max_tokens` must be generous or the tool call is never reached.
- **Escalation runtime uses a FIXED port** so ADR-032's cached escalation `IChatClient` reconnects after teardown→respawn.
- **Test gate:** run `env -u MEKO_API_KEY make test` (avoids the live-Meko smoke hang). `make build` is `TreatWarningsAsErrors`.

## Group 1 — ADR-034 (gate)

- **1.1 / 1.2 (DONE).** Wrote `docs/adrs/ADR-034-mlx-local-runtime.md`, accepted by user → Status: Accepted.
  - **Decision 1 (amends ADR-007):** `StopAsync(CancellationToken)` added to `IProviderExtension` with a **default no-op** → existing providers source-compatible, no edits. A provider that owns a spawned server kills it + releases its port on `StopAsync`. (Backs tasks 5.1 and 3.5.)
  - **Decision 2 (amends ADR-006):** composition-declared backends carry **standing spawn consent** — no per-call confirmation prompt for start/warm/respawn. Replaces ADR-007 D2's `tool.confirmRequest risk:high` gate **for composition-declared backends only**; interactive/ad-hoc use keeps the gate. (Enables `EscalationWarmingService` to warm/teardown repeatedly without prompting.)
  - **Decision 3 (amends ADR-032 D3):** escalation backend = fixed-port mlx runtime + activity-warming + idle-teardown. Warming is **additive/best-effort**; the escalation path's lazy `EnsureRunningAsync` is **retained** as the self-heal backstop. Three-layer split: **core** = neutral `ISessionActivityListener` (`OnSessionActivated`/`OnTurnStarted`, in-process, NOT on the RPC wire) fired by `SessionHandler` + `TurnHandler`; **daemon** = `EscalationWarmingService` + idle timer (routing policy home per ADR-027 D5); **provider** = `EnsureRunningAsync`/`StopAsync` mechanism, knows nothing of sessions.
  - Reviewer signed off; applied 5 polish nits (model-label wording, Amends header wording "provider-spawn confirmation gate", ADR-007 relationship bullet now notes the gate narrowing, added ADR-021/ADR-024 to Builds-on + relationship bullets, softened the recompile claim to source-compatible-via-default-method).
  - Idle-timeout exact default left as Open Question (lean ~10 min, configurable). Readiness `/health` vs completion and `reasoning`-field mapping are Open Questions to resolve during impl.
