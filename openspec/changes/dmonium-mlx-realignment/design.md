## Context

The dmonium menu-bar/dashboard app (`daemon/Daemon.App`, Swift) supervises the personal-assistant stack: it spawns the Network host (which runs the core `Daemon.cs`), the Dcal and Dmail servers, brings Tailscale up, and publishes a typed `HealthRegistry` of component health rows. Its settings surface writes `~/.dmon/config.yaml` (+ Keychain for secrets).

dmonium was built (ADR-028 era) around the **original** triage design (`daemon/BRIEF.md`): a small "e2b" tier on Ollama and a larger "reasoner" tier, each a configurable OpenAI-compatible HTTP endpoint (`DMON_E2B_URL`, `DMON_REASONER_URL`). Two subsequent accepted changes invalidated that model entirely:

- **ADR-032** dropped the `Tier`/`Reasoner` upfront dispatch — there is no "reasoner" anymore; the larger local model is reached only by first-line `think_harder` escalation.
- **ADR-034** replaced the externally-configured local endpoints with `Dmon.Providers.Mlx`: first-line `gemma-4-e4b-it-qat-OptiQ-4bit` on **fixed** port 8800 and escalation `gemma-4-26B-A4B-it-qat-nvfp4` on **fixed** port 8810, both **provider-spawned** with baked ports/model ids and owned-lifecycle readiness. The core `Daemon.cs` now reads only `GEMINI_API_KEY` + `DMON_EGRESS_MODEL`.

dmonium's Swift was never updated: `SettingsView.swift` still exposes Reasoner/E2B URL+model fields; `DaemonController.swift` still registers an "E2B Endpoint" probe (default Ollama `:11434`) and a "Reasoner Endpoint" probe (default `:8080`) as health rows 6–7; `ConfigStoreTests.swift` asserts on the removed vars. The `daemon-host` and `daemon-composition-root` standing specs and the daemon docs (`BRIEF.md`, `Daemon.App/README.md`) describe the same obsolete surface.

## Goals / Non-Goals

**Goals:**
- Remove the dead "reasoner" and "e2b configurable endpoint" surface from dmonium (settings + health probes + Swift tests).
- Bring `daemon-host` and `daemon-composition-root` standing specs in line with the shipped mlx composition and dmonium surface.
- Mark `daemon/BRIEF.md` as historical/superseded and fix the README prose.
- Leave the tree green: `make daemon-app` (Swift build + `DaemonAppTests`) and `openspec validate --strict`.

**Non-Goals:**
- No core, `Dmon.Providers.*`, or protocol changes — the core is already correct (verified: `Daemon.cs` uses `AddMlxFirstline`/`AddMlxEscalation` + `UseTriage`/`AddEscalation`/`AddEgress`/`AddEscalationWarming`).
- No new ADR — this conforms the daemon surface to already-accepted ADR-032/ADR-034.
- No new dmonium probing of the mlx fixed ports (design D1 rejects option B).
- No rewrite of `daemon/BRIEF.md`'s body — a superseded banner, not a re-authoring (preserves history).

## Decisions

### D1 — dmonium drops the inference-endpoint settings + the E2B/Reasoner health probes entirely (no replacement probe)

The core (`Dmon.Providers.Mlx`) owns spawning, readiness, idle-teardown, and respawn of both mlx runtimes on fixed ports. dmonium probing them would duplicate health the core already owns, and the mlx ports are baked provider defaults (not env-configured), so there is no user-facing endpoint to expose in settings. dmonium therefore stops health-probing local inference endpoints and drops the corresponding settings fields. It keeps the egress (Gemini) settings (`DMON_EGRESS_MODEL`, `DMON_EGRESS_THRESHOLD`, `GEMINI_API_KEY`) and the Dcal/Dmail/Network/Tailscale/calendar-sync components it actually supervises.

*Alternative B rejected:* re-point the two probes at the mlx fixed ports 8800/8810. This re-introduces a runner-specific assumption dmonium has no business owning (the core may change ports/quants), duplicates the core's readiness probe, and would have dmonium hard-coding provider internals. ADR-034 deliberately put mlx lifecycle/health behind the provider.

*Alternative C rejected:* collapse to one generic "local runtime" health row. Same duplication problem as B with less information; not worth the new wiring.

The health rows renumber: with the two inference probes removed, the registry's component order (currently …Mail(5), E2B(6), Reasoner(7), Egress(8)) closes the gap — Egress moves up. This is an internal ordering detail, not a contract.

### D2 — Standing specs restate behaviour, they don't merely delete

`daemon-composition-root`'s "wires triage routing with three backends" requirement is restated to the shipped composition (`AddMlxFirstline`/`AddMlxEscalation` + `UseTriage(MlxClient firstline)` / `AddEscalation(MlxClient escalation)` / `AddEgress` / `AddEscalationWarming`), and its credentials requirement is narrowed to the truth: only `GEMINI_API_KEY` + `DMON_EGRESS_MODEL` are env-sourced; the mlx runtimes use the provider's baked fixed-port + verified-quant defaults. `daemon-host`'s settings-fields and health-probe requirements drop the e2b/reasoner endpoint+model fields and probes. The unchanged parts (egress, Dcal/Dmail/Tailscale/calendar-sync/Network, secrets-in-Keychain) stay.

### D3 — `daemon/BRIEF.md` gets a superseded banner, not a rewrite

`BRIEF.md` is the *original* implementation brief — a historical artifact. Rewriting it to look like it always described the mlx design erases useful history and is needless churn. A prominent top banner marks it historical and points to ADR-032 (think_harder self-escalation replaced Tier/Reasoner) and ADR-034 (mlx replaced Ollama/oMLX) plus the current standing specs (`triage-routing`, `daemon-composition-root`, `mlx-provider`). The body is left intact.

## Risks / Trade-offs

- **Swift build/test environment may be unavailable in some contexts** → `make daemon-app` requires the Swift toolchain on macOS. The gate is `make daemon-app` (build + `DaemonAppTests`); if Swift is unavailable the change cannot be verified and must not be ticked — flag for human verification rather than guessing.
- **Removing settings fields is BREAKING to any hand-edited `~/.dmon/config.yaml`** → acceptable per the project's no-production-deployments / clean-break policy; stale `DMON_E2B_*`/`DMON_REASONER_*` keys in an existing config are simply ignored (not read), not an error.
- **Health-row renumbering** → internal `order:` integers only; no external contract depends on them. The `daemon-host` spec describes the component *set*, not fixed indices.
- **Spec drift could recur** if a future runner swap again updates the core but not dmonium → mitigated by this change tightening the specs to "the core owns inference-runtime health," reducing dmonium's runner-specific surface to zero.

## Migration Plan

1. Land the dmonium Swift edits (settings fields + probes + tests) — `make daemon-app` green.
2. Sync the `daemon-host` + `daemon-composition-root` standing specs (delta specs in this change drive the sync at archive).
3. Update `daemon/BRIEF.md` (banner) + `daemon/Daemon.App/README.md` (prose).

Rollback: pre-deployment; revert the branch. No data migration (stale config keys are ignored).

## Open Questions

(none — D1 resolves the only material design choice; option A chosen and justified.)
