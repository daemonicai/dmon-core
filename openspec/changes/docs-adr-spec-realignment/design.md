# Design — docs-adr-spec-realignment

## Context

Pure documentation/spec realignment. No runtime code changes. The work is mechanical but must be precise, because standing specs are the contract the apply-workflow agents read every block, and `openspec validate --strict` enforces that delta requirement headers match existing requirements exactly.

Two distinct edit channels, deliberately kept separate:

1. **Standing specs** (`openspec/specs/**`) — in this workflow these are *never* hand-edited during apply. They are corrected via **delta specs** under `changes/docs-adr-spec-realignment/specs/<cap>/spec.md`, which `openspec archive` syncs into the standing specs. So authoring the delta spec *is* the correction; the standing-spec file changes only at archive time.
2. **Plain docs** (`CLAUDE.md`, `README.md`) — not spec-governed, edited directly during apply as ordinary tasks.

## Goals / Non-Goals

**Goals**
- Every named stale reference (oMLX, reasoner backend, `IDaemonExtension`, `Dmon.Extensions`, `Daemon.BuiltinTools`, `Gateway`/`GatewayManager`, `Dmon.slnx`, `src/Dmon.Terminal`, broken ADR-001 link) is corrected to match the binding ADR / shipped code.
- Delta specs preserve the *unchanged* substance of each requirement — only the stale token/example/sentence changes. MODIFIED deltas copy the entire requirement block (all scenarios) so no detail is lost at archive.
- `openspec validate docs-adr-spec-realignment --strict` passes; `make build`/`make test` stay green.

**Non-Goals**
- No re-architecting of any spec. If a spec's *behaviour* looks wrong (as opposed to a stale name/example), that is a separate change — stop and ask.
- No touching correct "formerly / deleted / negated" statements (see proposal's out-of-scope list).
- No wire-contract renames (`gw`, `Dmon.Protocol.Gateway`, control frames stay per ADR-033).

## Key decisions

### D1 — MODIFIED, not REMOVED, for the stale scenarios
The oMLX scenarios (`provider-registry` "oMLX via Anthropic adapter", `auth` oMLX apiKey) live *inside* requirements that stay valid. Removing/replacing a scenario is expressed as a **MODIFIED requirement** (the requirement block is re-stated with the scenario rewritten to a current provider or dropped), not a REMOVED requirement. REMOVED is reserved for retiring a whole requirement, which none of these are.

### D2 — Replace oMLX examples with a current local provider, don't just delete
Where oMLX appears in an *example list* (`provider-registry:27` baseUrl targets, `auth:63` local providers, `provider-extension:32` ProviderName examples), replace it with a currently-shipped equivalent (`llama.cpp`, `Ollama`, `LM Studio`, or drop to leave the remaining valid examples) rather than leaving a two-item list that reads as if the set shrank. The generic capability is unchanged; only the illustrative names are refreshed.

### D3 — `daemon-composition-root` Purpose vs body
Only the **Purpose** sentence ("three backends (e2b local, local reasoner, gated cloud egress)") is stale; the requirement **body** already states the correct mlx-firstline / mlx-escalation / egress shape with "There is no `AddReasoner`/upfront-tier registration". The Purpose section is prose, not a `### Requirement:` block. **Open question for the worker/architect:** confirm whether the spec-driven delta mechanism syncs non-requirement Purpose prose at archive. If a delta spec cannot carry a Purpose-only edit, the Purpose correction must be applied as a direct standing-spec edit task at apply time (documented in the DEVLOG) rather than via delta. Resolve this before implementing the `daemon-composition-root` block.

### D4 — `daemon-host` Gateway→Network is a full-spec pass, its own block
The `Gateway`/`GatewayManager` prose spans ~30 line references across many requirements in `daemon-host/spec.md`. Treat it as a single dedicated block: every MODIFIED requirement in that spec re-stated with host-role prose renamed to `Dmon.Network`/`NetworkManager`, while leaving any wire/`gw` string intact. Cross-check against `package-publishing/spec.md:33` (already says `NetworkManager`) and `daemon-app-management` archived specs to match the exact shipped class name.

### D5 — README additions kept minimal
The monorepo-layout and Network/`ndmon` mentions are *additive* one-liners so the README stops predating ADR-025/033 — not a full rewrite. Keep to a sentence each, pointing at the buckets and the remote host, consistent with `monorepo-layout` and `remote-session-gateway` specs.

## Verification

- `openspec validate docs-adr-spec-realignment --strict` (delta headers match existing requirements exactly).
- `make build` clean (TreatWarningsAsErrors on) — a doc edit must not break any doc-linked build step.
- `env -u MEKO_API_KEY make test` green — proves no test references the corrected strings in a way that now fails.
- Manual grep gate after each block: the corrected stale token no longer appears in the touched file except where intentionally retained (wire strings, historical "formerly" statements).

## Risks

- **Delta header mismatch** — the most likely failure. Mitigation: copy requirement headers verbatim from the current standing spec; validate --strict after each block.
- **Over-reach** — accidentally rewording correct negation/"formerly" text. Mitigation: the proposal's explicit out-of-scope list; reviewer checks each delta touches only the stale token.
- **Purpose-prose sync (D3)** — resolve the mechanism before the `daemon-composition-root` block.
