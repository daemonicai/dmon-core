## Why

Across the recent wave of accepted ADRs (022 extension-contract collapse, 025 monorepo consolidation, 032 handler-initiated escalation, 033 Gateway→Network rename, 034 MLX runtime), several standing specs in `openspec/specs/` and two top-level docs (`CLAUDE.md`, `README.md`) were never brought back in line with what shipped. The result is documentation that contradicts the binding ADRs and the code: specs still name deleted types (`IDaemonExtension`, `Dmon.Extensions`), a removed provider (oMLX), a dropped backend (the reasoner), a renamed host (`Gateway`/`GatewayManager`), and stale build paths (`Dmon.slnx`, `src/Dmon.Terminal`).

This is the doc-only realignment follow-up (#7) from the 2026-07-06 repo audit. It changes no runtime behaviour — every edit makes a spec or doc *match* an already-binding ADR and the shipped code. Leaving it undone keeps the standing specs (the contract of record, which the architect/worker/reviewer read every block) actively misleading.

## What Changes

**Standing-spec corrections (via delta specs, synced at archive):**

- **`provider-registry`** — drop the removed **oMLX** from the OpenAI-adapter `baseUrl` example list; replace/retire the "oMLX via Anthropic adapter" scenario (oMLX no longer ships; ADR-034 replaced it with `Dmon.Providers.Mlx`). The generic capability (openai/anthropic adapter + `baseUrl`) is unchanged.
- **`auth`** — drop **oMLX** from the local-provider list and rewrite the oMLX `apiKey` scenario in terms of a current local provider. The optional-`apiKey`-for-networked-local-providers requirement is unchanged.
- **`provider-extension`** — rename the stale **`IDaemonExtension`** reference to **`IToolExtension`** (ADR-022) and drop **oMLX** from the `ProviderName` examples. `IProviderExtension` itself is confirmed still live in code — this spec is current, only its examples/names are stale.
- **`daemon-composition-root`** — fix the Purpose sentence that still describes "three backends (e2b local, local reasoner, gated cloud egress)" to the shipped first-line-mlx / escalation-mlx / egress shape (ADR-032/034); the requirement *body* is already correct. Reframe the `DMON_E2B_*`/`DMON_REASONER_*` scenario to the "former variable" absence framing already used by `daemon-host`.
- **`daemon-host`** *(added beyond the audit's named files — ADR-033)* — realign pervasive host-role prose **`Gateway`/`GatewayManager`** → **`Dmon.Network`/`NetworkManager`** to match ADR-033 and the already-correct `package-publishing` spec. Wire/contract strings (`gw` discriminator, `Dmon.Protocol.Gateway`, control frames) are intentionally **kept** per ADR-033 and are out of scope.

**Stray-file cleanup (apply-time deletion, not a spec-requirement change):**

- **Orphaned `builtin-tools/builtin-tools/spec.md`** — the **canonical** `openspec/specs/builtin-tools/spec.md` is *already correct* (`IToolExtension`, `Dmon.Tools.Builtin`, `.AddBuiltinTools()`, ADR-023). The stale `IDaemonExtension`/`Daemon.BuiltinTools` text survives only in a nested duplicate left behind by the archived `daemon-builtin-tools` change — a path openspec does not recognize. The fix is to **delete the orphan file**, not to author a delta (the canonical spec needs no edit).

**Plain-doc corrections (apply-time edits, not spec-governed):**

- **`CLAUDE.md` build section** — `Dmon.slnx` → `Everything.slnx` (two occurrences) and `src/Dmon.Terminal` → `frontends/Dmon.Terminal`.
- **`README.md`** — `IDaemonExtension`/`Dmon.Extensions` → `IToolExtension`/`Dmon.Abstractions` (two spots); fix the broken ADR-001 link (`ADR-001-llm-abstraction.md` → `ADR-001-llm-provider-abstraction.md`); add a brief mention of the monorepo bucket layout (ADR-025) and the `Dmon.Network`/`ndmon` remote host (ADR-033).

**Explicitly out of scope (verified correct — do not touch):**

- The CLAUDE.md ADR table (033/034/035 already present; ADR-017/030/031 correctly absent because they are only *Proposed*).
- All "formerly X" / deletion / negation statements across specs (`extension-model`, `monorepo-layout`, `package-publishing`, `triage-routing`, `daemon-host` E2B-absence, `mlx-provider`, memory "tier" text) — these are correct current text describing accomplished transitions.
- Wire/contract `gw`/`Dmon.Protocol.Gateway` strings (kept by ADR-033).
- The CLAUDE.md ADR-002 row's historical `IDmonExtension` mention (it records the original decision; ADR-022's row records the rename).

## Capabilities

### New Capabilities

_None — this change modifies existing standing specs and docs only._

### Modified Capabilities

- `provider-registry`: remove oMLX from adapter examples and the oMLX-via-Anthropic scenario.
- `auth`: remove oMLX from the local-provider list and rewrite its apiKey scenario.
- `provider-extension`: `IDaemonExtension`→`IToolExtension`; drop oMLX example provider names.
- `daemon-composition-root`: correct the Purpose backend list and the `DMON_E2B_*`/`DMON_REASONER_*` scenario framing.
- `daemon-host`: `Gateway`/`GatewayManager` host-role prose → `Dmon.Network`/`NetworkManager` (wire strings kept).

## Impact

- **Specs:** `openspec/specs/{provider-registry,auth,provider-extension,daemon-composition-root,builtin-tools,daemon-host}/spec.md` (via delta specs, applied at archive).
- **Docs:** `CLAUDE.md` (build section), `README.md`.
- **Code / runtime:** none. No source, test, protocol, or build behaviour changes.
- **ADRs:** none authored; every edit realigns to already-binding ADR-022/025/032/033/034.
- **Risk:** low — editorial. Primary gate is `openspec validate --strict` (delta headers must match existing requirements exactly) plus a green `make build`/`make test` to prove no doc edit broke a doc-linked check.
