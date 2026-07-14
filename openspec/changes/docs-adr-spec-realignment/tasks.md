# Tasks — docs-adr-spec-realignment

This is a **doc-only** realignment. Per CLAUDE.md ("Doc-only spec/design realignments … are the orchestrator's to edit — agents don't"), the orchestrator applies these edits directly at `/opsx:apply`; no architect/worker/reviewer feature loop is required. Standing-spec **requirement** corrections are carried by the delta specs under `specs/` (synced into `openspec/specs/` at archive); **Purpose-prose** and non-spec docs are edited directly.

## 1. Spec deltas define the standing-spec end-state (authored; verified at gate)

- [x] 1.1 `specs/provider-registry/spec.md` — MODIFIED "Supported provider adapters": drop removed **oMLX** from the `openai` `baseUrl` example; generalize the "oMLX via Anthropic adapter" scenario to an Anthropic-compatible-local-endpoint scenario. Generic adapter+baseUrl capability unchanged.
- [x] 1.2 `specs/auth/spec.md` — MODIFIED "Local providers support optional API key": drop **oMLX** from the local-provider list and rewrite its `apiKey` scenario in terms of a current local provider. Optional-apiKey requirement unchanged.
- [x] 1.3 `specs/provider-extension/spec.md` — MODIFIED "ProviderName is stable and unique": replace the **oMLX** `ProviderName` example with current providers. (`IProviderExtension` confirmed still live in code — 29 files.)
- [x] 1.4 `specs/daemon-host/spec.md` — RENAMED the "Gateway process is started…" requirement to "Network host process is started…"; MODIFIED the five requirements carrying host-role prose so **Gateway → Network host** and **GatewayManager → NetworkManager** (shipped Swift class is `NetworkManager`, 22 refs; zero `Gateway`). Wire/contract strings (`gw`, `Dmon.Protocol.Gateway`, control frames) are NOT present here and are untouched (ADR-033).

## 2. Direct standing-spec Purpose-prose edits (delta cannot carry `## Purpose`)

- [x] 2.1 `openspec/specs/provider-extension/spec.md` Purpose (line ~8) — `IDaemonExtension` → `IToolExtension` (ADR-022; `IDaemonExtension` is in zero `.cs` files); drop **oMLX** from the "(Ollama, oMLX, LM Studio, llama.cpp)" example list.
- [x] 2.2 `openspec/specs/daemon-composition-root/spec.md` Purpose (line ~3) — "three backends (e2b local, local reasoner, gated cloud egress)" → "three backends (first-line mlx, escalation mlx, gated cloud egress)" (ADR-032/034; the requirement **body** at line 8 is already correct). Leave the line-49 scenario's `DMON_E2B_*`/`DMON_REASONER_*` as a correct negative assertion.
- [ ] 2.3 At archive, confirm the Purpose direct-edits survived the delta sync (re-apply if `openspec archive` regenerated `provider-extension`/`daemon-composition-root` from source). Record the outcome in DEVLOG (resolves design D3).

## 3. Stray-file cleanup

- [x] 3.1 Delete the orphan `openspec/specs/builtin-tools/builtin-tools/spec.md` (stale `IDaemonExtension`/`Daemon.BuiltinTools` duplicate left by the archived `daemon-builtin-tools` change; the canonical `openspec/specs/builtin-tools/spec.md` is already correct and openspec-recognized).

## 4. CLAUDE.md build section

- [x] 4.1 Line ~139 — "The solution is `Dmon.slnx`" → `Everything.slnx`.
- [x] 4.2 Line ~146 — `dotnet build Dmon.slnx -c Release` → `dotnet build Everything.slnx -c Release`.
- [x] 4.3 Line ~148 — `dotnet run --project src/Dmon.Terminal` → `dotnet run --project frontends/Dmon.Terminal`.

## 5. README

- [x] 5.1 Lines ~39, ~77 — `IDaemonExtension` (from `Dmon.Extensions`) → `IToolExtension` (from `Dmon.Abstractions`) (ADR-022; both old names deleted).
- [x] 5.2 Line ~76 — fix the broken ADR-001 link `./docs/adrs/ADR-001-llm-abstraction.md` → `./docs/adrs/ADR-001-llm-provider-abstraction.md`.
- [x] 5.3 Add a one-line mention each of the monorepo bucket layout (`core/ providers/ tools/ memory/ frontends/ daemon/ services/`; ADR-025) and the `Dmon.Network` / `ndmon` remote host (ADR-033), so the README no longer predates them.

## 6. Gates

- [x] 6.1 `openspec validate docs-adr-spec-realignment --strict` passes (delta requirement headers match existing requirements exactly).
- [x] 6.2 `make build` clean (TreatWarningsAsErrors on) — no doc-linked build step broken.
- [x] 6.3 `env -u MEKO_API_KEY make test` green.
- [x] 6.4 Grep gate: `oMLX`, `IDaemonExtension`, `Dmon.Extensions`, `Daemon.BuiltinTools`, `Dmon.slnx`, `src/Dmon.Terminal`, `GatewayManager` no longer appear in the touched files except where intentionally retained (correct "formerly"/negation statements, wire strings, historical ADR-002/033 table rows).
