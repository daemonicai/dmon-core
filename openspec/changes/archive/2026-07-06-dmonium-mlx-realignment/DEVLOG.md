# DEVLOG — dmonium-mlx-realignment

Status: **all 8 tasks complete** (1.1–5.2), single block, one commit. Reviewer signed off clean.

## Block 1 (tasks 1.1–5.2) — whole change in one commit

The architect scoped the entire change as one coherent, independently-gate-passing block: strip the dead E2B/Reasoner local-inference surface out of the dmonium Swift app, realign its tests and docs. Swift + docs only; no C#.

### What changed
- **`SettingsView.swift`** — removed `e2bURL`/`reasonerURL`/`e2bModel`/`reasonerModel` state, their Inference-section `TextField`/`.help` UI, and their `loadFromStore()`/`persistAndRestart()` load-save wiring for `DMON_E2B_URL`/`DMON_REASONER_URL`/`DMON_E2B_MODEL`/`DMON_REASONER_MODEL`. Retained: `DMON_EGRESS_MODEL`, `DMON_EGRESS_THRESHOLD`, Gemini key, Dmail/Calendar fields, sync interval, `recurrenceHorizon`, Dcal/Dmail/Network server paths. Pure deletion (0 added / 20 removed).
- **`DaemonController.swift`** — removed `e2bProbe` (`DMON_E2B_URL`→`:11434`) and `reasonerProbe` (`DMON_REASONER_URL`→`:8080/v1`): declarations, `.start()` calls, `healthRegistry.register(...)`. Renumbered `egressProbe` `order: 8`→`order: 6`; health rows now contiguous `0…6` (Network 0 → Egress 6). Updated ordering comment + bootstrap docstring ("nine … 0–8" → "seven … 0–6").
- **`ConfigStoreTests.swift`** — `ConfigStore` is a generic key/value YAML store; the E2B/Reasoner names were only *sample keys*. Swapped for retained keys (`DMON_EGRESS_MODEL`, `DMON_NETWORK_PATH`, `DMAIL_BASE_URL`, `DMON_EGRESS_THRESHOLD`, `DMON_DCAL_SERVER_PATH`); multi-colon URL fixture moved off port `8080/v1` → `5280/v1` to fully clear the grep. All parsing assertions preserved (first-colon split, comment/blank skip, secret omission, Keychain token). No store-level key filtering added (D1 Risks: stale keys are *ignored*, not stripped).
- **`DaemonControllerTests.swift`** — renamed `testBootstrap_registersNineComponents_afterOneCall` → `…registersSevenComponents…` (name/comment only; idempotence assertion unchanged).
- **`BRIEF.md`** — banner-only addition (11 added / 0 removed) at the very top marking it as the *original/superseded* tiered-inference design, pointing to ADR-032 (think_harder self-escalation, no reasoner) + ADR-034 (`Dmon.Providers.Mlx`, fixed ports) and the standing specs `triage-routing`/`daemon-composition-root`/`mlx-provider`. Body intact (D3).
- **`README.md`** — dropped dead `DMON_E2B_URL`/`DMON_REASONER_URL` names and the "three `DMON_*_MODEL` IDs" claim; states dmonium no longer probes local inference (mlx provider owns lifecycle/readiness).

### Decisions honoured
- **D1** — no replacement probe, no re-point at mlx 8800/8810, no generic "local runtime" row. dmonium probes only egress now.
- **D3** — `BRIEF.md` superseded banner only; body preserved.
- Scope: no .NET/C# source touched; standing specs under `openspec/specs/` left for orchestrator sync at archive (D2).

### Gate outcomes
- `make daemon-app` (swift build -c release) — clean.
- `swift test --package-path daemon/Daemon.App` — **72/72, 0 failures.** (Note: an orphaned duplicate `swift test` from the worker's background wait contended on the same `.build` lock and stalled both; killing the stale PID let a single run finish instantly. Not a code issue.)
- `openspec validate dmonium-mlx-realignment --strict` — valid.
- `make build` — 0 warnings / 0 errors.
- `env -u MEKO_API_KEY make test` — all assemblies green (Core 606 +1 skip, Network 209, Terminal 187, Mtplx 32 +1 skip, Dcal 14, WebSearch 12, Builtin 102, Desktop 51, Daemon.Routing 44).

### Carry-overs
- Standing-spec sync (`daemon-host`, `daemon-composition-root`) happens at archive time from this change's delta specs — not part of implementation tasks.
- No human-in-the-loop verification outstanding for this block (Swift toolchain was available; gates ran green).
