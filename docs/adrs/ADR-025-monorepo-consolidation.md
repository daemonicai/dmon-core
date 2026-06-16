# ADR-025: Monorepo Consolidation and Per-Area Workflow Topology

**Date:** 2026-06-15
**Status:** Accepted
**Builds on:** ADR-019 (file-based-program composition, `#:package` restore), ADR-022 (composition root as a feature), ADR-023 (granular implementation packages, naming families, prebuilt stock default), ADR-024 (protocol-cycle versioning — shared `Major.Minor`, independent patch).
**Relationship:** Provides the *physical structure* that ADR-023's granular packages and ADR-024's versioning model assume. Does not contradict any accepted ADR; it relocates the first-party code into one repository and defines how work is organised across it.

## Context

The first-party dmon code is spread across separate `daemonicai/*` repositories (`dmon-core`, `dmon-llama-cpp`, `dmon-websearch`, `dmail`, `dmon-meko`, plus host/companion repos). ADR-023 made every provider/tool/middleware a granular package and ADR-024 settled how those packages version together. Both assume a coherent first-party set that ships and breaks in lockstep cycles — which is far easier to author, refactor, and release atomically when the code lives in **one repository**.

A monorepo also unlocks the working model the maintainer wants: per-area solutions for fast scoped builds, git-worktree parallelism (one agent in `tools/`, another in `providers/`), path-filtered CI, and per-package releases — all of which are awkward across polyrepos.

The risk a monorepo usually carries — a single tangled history and a flattened spec/decision namespace — is avoided here by importing each repo *as a self-contained area* rather than dissolving it (see Decisions 5–6). The collision survey confirmed this is cheap: across all incoming repos there is exactly **one** capability-name clash (`memory`, dmon-core ⟷ dmon-meko) and **no** ADR clashes (only dmon-core carries ADRs).

## Decision

1. **Consolidate the first-party .NET repos into one monorepo.** dmon-core is the seed; the other first-party .NET repos are imported under top-level area directories (Decision 11), with history preserved (mechanics deferred — Open Question A).

2. **Top-level buckets by ADR-023 role.**
   - `core/` — contracts + engine: `Dmon.Abstractions`, `Dmon.Protocol`, `Dmon.Core` (`dmoncore`), `Dmon.Runtime`, `Dmon.Protocol.SchemaGen`.
   - `providers/` — `Anthropic`, `OpenAI`, `Gemini`, `Ollama`, `Omlx`, `LlamaCpp`.
   - `tools/` — `Builtin`, `WebSearch`, `Dmail`.
   - `middleware/` — `Memory`, `Memory.Meko`.
   - `frontends/` — host apps (Tier 2 per ADR-024): `Terminal`, `Gateway`, `dmonium`, future Avalonia.
   - `samples/` — composition-root `Dmon.cs` examples + the prebuilt stock default core (ADR-023 D8).
   - `libs/` — shared non-package infrastructure. **Provisional/likely-unnecessary:** `dcli` was its main candidate but stays external (Decision 11); the bucket is only created if something else (e.g. `memlite`) folds in (Open Question B).

3. **Per-area `.slnx` + a root `Everything.slnx`.** Each bucket has its own solution for fast scoped build/test/edit; `Everything.slnx` is the superset that builds/tests/edits the whole repo.

4. **Intra-repo references are `ProjectReference`; `PackageReference` is reserved for genuinely external/third-party consumers.** Atomic cross-cutting refactors (change a contract + every consumer in one commit) are the point of the monorepo. Per-area `.slnx` files reference sibling area projects by path. This also sidesteps the global-NuGet-cache clobber hazard for everyday dev (Decision 12).

5. **Hybrid openspec roots.** A **root `openspec/`** owns system/cross-cutting capabilities and changes; each area keeps its **own `openspec/`** (imported intact) for component-local capabilities and changes. **Determining rule:** *touches the wire protocol, cross-area contracts, or system behaviour → root; confined to one package → that package's area.* The lone `memory` collision resolves under this rule with no rename (engine-side abstraction at root; meko's concrete impl under `middleware/memory-meko/openspec`).

6. **ADRs follow the same boundary rule.** Global **system** ADRs live at root `docs/adrs/` (the existing ADR-001..NNN sequence, uncontested). A decision confined to one package may live as a **component** ADR under that area (its own number space). New cross-cutting ADRs are authored on `main` by the orchestrator and merged before dependent change-branches proliferate (keeps the global sequence collision-free).

7. **Parallel work via git worktrees, scoped by change kind.** *Component* changes run in a per-area worktree on `change/<area>-<slug>` — disjoint files, disjoint area openspec roots, disjoint `bin/obj/build`, separate PRs (e.g. one agent in `tools/`, another in `providers/`). *System/cross-cutting* changes run on `main` as a single coherent branch that may touch many areas and are **not** parallelised. Each worktree is a full checkout over one shared `.git`.

8. **Nested `Directory.Build.props` / `Directory.Packages.props` per area** (importing the root), so area-local build settings and package versions live in the area and only genuine global changes touch root config. Root config (`Directory.*.props`, `Everything.slnx`) is the deliberate, rarely-hit serialization point between parallel worktrees.

9. **Dependency-aware, path-filtered CI.** A PR touching only one area builds/tests that area's `.slnx`. A change to `core/**` (upstream of everything via Decision 4) or to root config builds/tests `Everything.slnx`. Naive per-directory filtering is insufficient because ProjectReferences make `core/` changes ripple downstream; the filter must encode "core ⇒ all."

10. **Two-family release matrix, triggered per package (ADR-024).** Per-package tag-prefix tags (e.g. `providers/anthropic-v0.2.5`) drive releases; a protocol cycle tags the whole set at `X.Y.0`. Release artifacts split into a **NuGet family** (core + providers + tools + middleware + the `dmon` Terminal tool) published with `dotnet nuget push`, and an **app-artifact family** (Gateway daemon publish, dmonium `.app`/`.dmg`, future Avalonia bundles) produced by packaging jobs. The trigger model is shared with Decision 9's CI.

11. **Repo landing tiers.**
    - **Fold in** (first-party .NET): `dmon-core` (splits across buckets), `dmon-llama-cpp` → `providers/llama-cpp` (rename to `Dmon.Providers.LlamaCpp`, ADR-023 D3), `dmon-websearch` → `tools/`, `dmail` → `tools/` (the `Dmon.Extensions.Dmail` package → `Dmon.Tools.Dmail`; the standalone Dmail *server* is out of scope), `dmon-meko` → `middleware/memory-meko`.
    - **External dependency (stays out, consumed as a published NuGet package):** `dcli` — the TUI toolkit; `dmon-core` already `PackageReference`s it. Not grafted; its 95-commit history stays in its own repo. (`memlite`/`memlite-pi` are likely the same — pending Open Question B.)
    - **Discuss** (Open Question B): `dmonium`, `dmon-dev`, `memlite`/`memlite-pi`.
    - **Keep separate** (different language / different product): `dmon-swift` (Swift), `dmon` (Unity/python/ts), `daemon` (empty).

12. **NuGet-cache clobber is avoided in dev, isolated in CI.** Everyday builds use ProjectReferences (Decision 4) and never touch the global NuGet cache. Tests that exercise the *packaged* form (restore-from-feed, composition smoke tests) are serialized or use per-worktree isolated feeds — a known stale-IL hazard (the `dmon*` cache-glob lesson).

13. **Import strategy and phased execution.** `dmon-core` **is the seed** (its native history is retained; its projects move into buckets via history-following `git mv`). Satellites graft in via **`git filter-repo --to-subdirectory-filter <area>` + `git merge --allow-unrelated-histories`** (trivial repos with no history worth keeping — e.g. `dmon-websearch`, 1 commit — are copied in a normal commit instead). Each satellite is **stabilised on its own `main` before grafting** (land any feature branch first; never graft a WIP branch). Source repos are **archived read-only** after import (GitHub PR/issue metadata does not travel with git history; the archived repo is its record). Execution is **strictly phased**: **Phase 0** reorganises the existing `dmon-core` projects into buckets (+ `Everything.slnx`, per-area `.slnx`, nested props, ghost-dir cleanup) and **must precede** worktree-per-area parallelism (Decision 7); **Phases 1…N** graft one satellite each, keeping CI green at every step; a **final phase** stands up the path-filtered CI (Decision 9) and release matrix (Decision 10) and archives the sources. Each phase is its own OpenSpec change; the step-by-step command sequence lives in those changes' `design.md`/`tasks.md`, not here.

## Consequences

- **Atomic cross-cutting change becomes trivial** — contracts + every consumer move in one commit/PR, the chief reason to consolidate.
- **Component work parallelises cleanly** — worktree-per-area with isolated build dirs and openspec roots; the maintainer's "agent in tools, agent in providers" model works with a collision surface reduced to root config + root openspec.
- **History and per-component decision records survive** — areas import with their `openspec/` and `CLAUDE.md` intact; the hybrid structure is obtained by *not flattening* rather than by merging.
- **CI and releases share one per-area trigger model** consistent with ADR-024's versioning.
- **More moving parts in one repo** — one clone is larger; `Everything.slnx` is heavy; contributors must learn the root↔area boundary rule. Mitigated by per-area `.slnx` and per-area `CLAUDE.md`.
- **The polyrepo PR/issue history of imported repos** must be reconciled or archived (Open Question A).

## Alternatives

- **Status quo polyrepo.** Rejected: atomic cross-cutting change (the ADR-023/024 lockstep cycle) is painful across repos; per-area CI/worktree/release decoupling is achievable *within* a monorepo without giving up coordinated cycles.
- **Monorepo with a single flattened openspec root.** Rejected: forces renaming `memory`, disambiguating generic capability names (`agent-api`, `test-harness`), renumbering change slugs, and mangling imported history — all to gain a single `openspec validate` invocation that per-area `cd` + `CLAUDE.md` replace cheaply.
- **`PackageReference` between areas inside the monorepo.** Rejected for dev: defeats atomic refactors, needs a local feed for unreleased contract changes, and re-introduces the cache-clobber hazard. Kept only for external/third-party consumers.

## Open Questions

- **A. Import mechanics & history (⑤).** **Resolved — see Decision 13** (dmon-core seed · filter-repo + `--allow-unrelated-histories` · stabilise-on-`main`-first · archive sources read-only · phased execution). Per-area config reconciliation rule is in the Consequences/import-reconciliation table of the eventual Phase change.
- **B. `dmonium` / `dmon-swift` / `memlite` placement.** (`dcli` **resolved** — external dep, Decision 11.) `dmonium` is a macOS frontend (`frontends/`, or its own app repo?); `dmon-swift` is a Swift frontend — does `frontends/` become polyglot (Swift outside `Everything.slnx`) or does Swift stay separate?; `memlite`/`memlite-pi` likely external like `dcli` — confirm.
- **C. Cross-area *non-core* changes.** When a change spans two non-core areas (e.g. a sub-agent tool bundling a provider, ADR-023 D7), does it go to root openspec or the primary area's? Pick a default.
- **D. `LLamaSharp` duplication.** `dmon-core` already references `LLamaSharp` while `dmon-llama-cpp` is a separate provider — reconcile into a single `Dmon.Providers.LlamaCpp` on import.
- **E. Hosts solution split.** Whether `frontends/` warrants one `frontends.slnx` or per-frontend solutions, given heterogeneous artifacts (tool / daemon / `.app`).

## Relationship to other ADRs

- **ADR-023** — supplies the role taxonomy the buckets mirror (Decision 2) and the naming families used on import (Decision 11). Its prebuilt stock default (D8) lands in `samples/`.
- **ADR-024** — supplies the versioning and release-trigger model that Decisions 9–10 operationalise; frontends are its Tier 2.
- **ADR-019/022** — composition-root authoring is unchanged; the monorepo only changes where the packages' *sources* live, not how an authored `Dmon.cs` consumes them.
- **Apply-workflow (`CLAUDE.md`)** — the orchestrator/worker/reviewer model extends to per-area worktrees (Decision 7); each area's imported `CLAUDE.md` scopes the workflow to that area's openspec root.
