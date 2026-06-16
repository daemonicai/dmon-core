## Context

ADR-026 (accepted, on `main`) decides memory is its own top-level bucket, not middleware, amending ADR-025 D2/5/6/11. This change is the next monorepo consolidation phase (after Phase 0 reorg, Phase 1 llama-cpp graft, Phase 2 dmail graft): it (a) moves the already-present `middleware/Dmon.Memory` into the new `memory/` bucket, and (b) grafts the `dmon-meko` satellite's long-term tier as `memory/Dmon.Memory.Meko`.

Current state:
- `middleware/Dmon.Memory` — local short-term tier (sqlite-vec + FTS5 + local embeddings) + `IMemory` facade; the only member of `middleware/`. Its test project is at `test/Dmon.Memory.Tests` (repo `test/` convention).
- `dmon-meko` (at `~/github/daemonicai/dmon-meko`) — `src/Dmon.Memory.Meko` (`ILongTermMemory` over Meko MCP) + `test/Dmon.Memory.Meko.Tests`. `main` is `8ed0886` (PR #1 merged, consolidated) **but** carries an unstarted change `add-memory-abstraction` (0/36).
- `Dmon.Abstractions.Memory` contracts live in `core/Dmon.Abstractions` and STAY there.
- The graft recipe is established (Phase 1/2); its gotchas are folded into Decisions below.

## Goals / Non-Goals

**Goals:**
- Realise ADR-026: a `memory/` bucket holding `Dmon.Memory` + `Dmon.Memory.Meko`, with `middleware/` gone (memberless role).
- Graft `dmon-meko`'s long-term tier with history preserved, rewired to monorepo conventions, gates green at each step.
- Update the `monorepo-layout` standing contract; author the new `memory-meko` capability spec.

**Non-Goals:**
- No type/API rename (`Dmon.Memory.Meko` keeps its name; contracts unchanged).
- Not importing dmon-meko's `add-memory-abstraction` refactor (lands later as its own monorepo change).
- Not importing the Dmail-style server or the satellite's own `openspec/` server-side capabilities.
- No new behaviour for the memory tiers — placement + graft only.

## Decisions

**D1 — `memory/` bucket, contracts stay in `core/`.** Move implementations only (`Dmon.Memory`, then graft `Dmon.Memory.Meko`); `Dmon.Abstractions.Memory` stays in `core/Dmon.Abstractions`. Rationale: ADR-026 D2/D3 — memory is contract-set + N backends + facade; the contract surface is a `core/` concern like every other abstraction. *Alternative considered:* move the whole memory namespace including abstractions to `memory/` — rejected; it would split `Dmon.Abstractions` and break ADR-025 D2's placement of contracts in `core/`.

**D2 — In-tree move precedes graft (Group 1 before Group 2).** Group 1 establishes the `memory/` bucket + `memory.slnx` by moving the existing `Dmon.Memory`; Group 2 grafts into the now-existing bucket. Rationale: keeps each group independently green and the graft lands in a stable target. *Alternative:* graft first then move both — rejected; more churn, two moves of the same files.

**D3 — `middleware/` directory and `middleware.slnx` are removed, role retained.** Once `Dmon.Memory` leaves, `middleware/` is memberless; per ADR-026 D4 and the updated `monorepo-layout` spec, a memberless role has no directory and no `.slnx`. The `Dmon.Middleware.<Name>` naming family stays defined for when the first `IDmonMiddleware` ships. Rationale: honest tree; bucket meaning restored. *Alternative:* keep an empty `middleware/` with a placeholder `.slnx` — rejected; an empty solution is noise and would fail "area solution builds" scenarios.

**D4 — Graft mechanics (established recipe + Phase 1/2 gotchas).**
1. Pick the source ref: default `dmon-meko` **`main`** (`8ed0886`) as-is (see D6).
2. `git clone --no-local -b main <dmon-meko> /tmp/meko-graft` — `--no-local -b <branch>` is mandatory: a local clone hardlinks (filter-repo rejects "not fresh"); a post-clone `git checkout` adds a second reflog entry (filter-repo aborts "expected at most one entry in the reflog"). Clone the target branch directly.
3. `uvx git-filter-repo` (not installed globally; run ad hoc) with `--path src/Dmon.Memory.Meko --path test/Dmon.Memory.Meko.Tests` and `--path-rename` to remap to `memory/Dmon.Memory.Meko/` and `test/Dmon.Memory.Meko.Tests/`.
4. Add as a remote + `git merge --allow-unrelated-histories`.
5. Verify history preserved with `git log --follow memory/Dmon.Memory.Meko`.

**D5 — Rewire to monorepo conventions.**
- `Dmon.Abstractions` PackageReference → ProjectReference (`core/Dmon.Abstractions`). Add an explicit `core/Dmon.Protocol` ProjectReference **only if** the Meko code uses `Dmon.Protocol` types directly (don't rely on transitive flow — a Phase 2 lesson); audit `using`s first.
- Third-party pins (ModelContextProtocol, Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.{Options,Configuration,DependencyInjection,Logging}.Abstractions) move to root CPM `Directory.Packages.props`; strip inline `Version=` from the grafted `.csproj`. Reuse existing pins where they already exist (e.g. M.E.AI.Abstractions is already pinned for core); add only genuinely new ones (ModelContextProtocol likely new).
- Test csproj: conform to the repo's test convention (xunit, `test/` location). If the grafted test project diverges, scaffold a fresh csproj from a sibling (e.g. `test/Dmon.Memory.Tests`) and keep the imported test source.
- Wire both projects into `memory.slnx` and `Everything.slnx`.
- Verify `IsPackable`/skew-guard: the package must pack on the protocol cycle (`Dmon.Memory.Meko`, `0.2.x`), skew-guard reading `core/Dmon.Protocol/ProtocolVersion.cs` intact.

**D6 — Graft current `main` as-is; defer `add-memory-abstraction`.** dmon-meko's `add-memory-abstraction` (0/36) is a large unstarted refactor of the memory abstraction. Default: graft `main` as-is and let that refactor land later as its own monorepo change against live source (the Omlx/llama-cpp/dmail pattern — port against current source, not the satellite's stale design). *This is the one decision point flagged for the user* — if that refactor is actually a prerequisite (it is not, on inspection: the current tier builds and tests standalone against `Dmon.Abstractions`), graft it first on the satellite. Recommendation: graft as-is.

**D7 — `memory-meko` capability spec authored here, synced to root `openspec/specs/` on archive.** Follows the Phase 1/2 provenance (llamacpp-provider, dmail-tool). ADR-025 D5/6's *per-area* `openspec/` roots remain unrealized in practice (Phase 0–2 all used root specs; satellites' own openspec was not imported); this change follows suit and does not stand up a `memory/openspec/` tree. Standing up per-area openspec is deferred as a separate concern.

## Risks / Trade-offs

- **Skew-guard / pack break after rewire** → run `make build` + a pack dry-run each group; keep the skew-guard MSBuild target and `core/Dmon.Protocol/ProtocolVersion.cs` path intact; verify package `Major.Minor` matches `ProtocolVersion.Current`.
- **filter-repo aborts (reflog / not-fresh)** → use exactly `git clone --no-local -b main`; never `checkout` post-clone (Phase 2 lesson).
- **Transitive-reference compile break** → audit the grafted `using`s; add explicit ProjectReferences for any `core/` package whose types are used directly (don't lean on transitive flow).
- **Hidden inline package pins after CPM move** → grep the grafted `.csproj` for `Version=` and the rename/port grep MUST include `*.md` (README `#:package` pins, namespaces) — a Phase 2 lesson.
- **Live MCP tests run in CI** → the live smoke test is environment-gated (`Category=Live`, skipped without `MEKO_API_KEY`); confirm it stays gated so `make test` is offline/green.
- **Removing `middleware/` breaks a lingering reference** → grep `Everything.slnx`, the `Makefile`, and any docs for `middleware/` / `middleware.slnx` before deleting.

## Migration Plan

1. **Group 1** — move `middleware/Dmon.Memory` → `memory/Dmon.Memory` (+ test project), `git mv` history-preserving; create `memory.slnx`; remove `middleware/` + `middleware.slnx`; repath `Everything.slnx` + `Makefile`; fix ProjectReferences. Gate: `make build`/`make test` green, `validate --strict`. Commit.
2. **Group 2** — import dmon-meko via the D4 recipe (own commit for the merge). Gate: build (errors expected until rewire — keep merge commit separate). 
3. **Group 3** — rewire (D5): ProjectReferences, CPM pins, test csproj, slnx wiring, pack/skew-guard. Gate: full green + pack dry-run. Commit.
4. **Group 4** — author/sync the `memory-meko` spec delta + `monorepo-layout` delta validation. Gate: `validate --strict`. Commit.
Rollback: each group is its own commit on `change/graft-meko-memory`; revert the group commit. The source `dmon-meko` repo is untouched until archive (tagged `absorbed-into-dmon-core` only at the end, per Phase 1/2).

## Open Questions

- **dmon-meko `add-memory-abstraction` (0/36) timing** — default graft-as-is (D6); confirm with the user if they intend that refactor to land before the graft. Recommendation: defer.
- **Per-area `openspec/` standup** — ADR-025 D5/6 envisions per-area openspec roots; none exist yet. Out of scope here (D7); flagged for a future structural change.
