# ADR-024: Protocol-Cycle Release Versioning (shared Major.Minor, independent patch)

**Date:** 2026-06-15
**Status:** Accepted
**Builds on:** ADR-011 (3-part protocol-keyed version scheme D5, `agentReady` skew gate D6, cadence-independence D8/D9), ADR-019 (file-based-program composition, `#:package` restore), ADR-023 (granular implementation packages, lockstep versioning D5, prebuilt stock default D8).
**Amends:** ADR-011 D5 and ADR-023 D5 — both made *all three* version parts move in lockstep across the first-party set. This ADR keeps `Major.Minor` lockstep (and protocol-keyed) but **frees the patch (`Z`) to vary per package**, so packages release on independent cadences *within* a protocol cycle.

## Context

ADR-011 D5 and ADR-023 D5 version the entire first-party package set in lockstep on `dmoncore`'s protocol-keyed `Major.Minor` line: one git tag, one MinVer-derived version, one number (`@0.2.*`) pinned across every `#:package` line in an authored `Dmon.cs`. The `Directory.Build.props` skew-guard enforces this mechanically — it errors any packable project whose `Major.Minor` ≠ `ProtocolVersion.Current` (`"0.2"` today).

That model is about to meet a **monorepo** consolidation (the `dmon-*` repositories merge under one root with per-area solutions — `core/`, `providers/`, `tools/`, `middleware/`, `frontends/` — and an `Everything.slnx`; designed in a separate, still-open change). The monorepo wants per-area CI, per-area release triggers, and parallel work via git worktrees. Strict three-part lockstep fights that: a one-line fix in `Dmon.Tools.Dmail` re-publishes *every* package at a new version, byte-identical except the version field, and a hot fix to one provider cannot ship without re-releasing the world.

The opposite extreme — fully independent SemVer per package (each pinning `Dmon.Abstractions@X.Y.*`, as ADR-023 already permits *third parties*) — was considered and rejected (Alternatives): it dissolves the universal `@X.Y.*` pin, replaces the restore-time incompatibility guarantee with per-package dependency-range bookkeeping, and turns a 1-dimensional compatibility story into an N-dimensional one. The value ADR-023 D5 prized — "one number resolves one coherent graph" — is worth keeping.

This ADR takes the middle: **lock the cycle, free the patch.**

## Decision

1. **Version anatomy.** Every first-party package version is `X.Y.Z` where **`X.Y` = the wire protocol `Major.Minor`** (lockstep across the entire first-party set — contracts, engine, providers, tools, middleware, and frontend host apps) and **`Z` = patch, owned independently by each package.**

2. **`X.Y` is a cycle, lockstep and protocol-keyed.** Bumping the protocol `Major` or `Minor` opens a new cycle: **every first-party package re-releases at `X.Y.0` simultaneously — including packages with no functional change.** The `X.Y.0` wave is the coherent cycle marker; after it, all packages share the new `X.Y` floor. `Major.Minor` is never bumped by an individual package.

3. **`Z` floats per package within a cycle.** Mid-cycle, each package increments its own patch independently as it changes: `Dmon.Providers.Anthropic@0.2.5` may sit alongside `Dmon.Tools.Dmail@0.2.1` in the same cycle. A package that does not change after `X.Y.0` keeps its `X.Y.0` patch.

4. **Mid-cycle releases are backward-compatible by construction; breaking changes are bound to cycle boundaries.** Because a package cannot bump its own `Major`/`Minor`, it cannot express a breaking change to its own surface (e.g. a `Use*`/`Add*` verb signature, ADR-023 D4) within a cycle. Any first-party breaking change waits for — or triggers — a protocol `Minor`/`Major` bump. The protocol cycle is therefore the **single, predictable breaking-change cadence** for the whole first-party ecosystem; within `X.Y.*` consumers can rely on nothing breaking.

5. **The universal `@X.Y.*` pin and ADR-023 D5's guarantees are retained.** Because all packages share `Major.Minor`, an authored `Dmon.cs` still pins `@0.2.*` on every `#:package` line and NuGet resolves one coherent graph at each package's latest patch. The restore-time incompatibility guarantee is unchanged (a `0.3` package cannot satisfy `@0.2.*`). **No meta/bundle package is required** to reconstruct the one-pin authoring UX — the shared `Major.Minor` provides it natively.

6. **Two compatibility-enforcement points, by package kind.** Composable libraries (contracts, engine, providers, tools, middleware) enforce compatibility at **`dotnet restore`** via the shared `Major.Minor`. Frontend host apps (Terminal, Gateway, future Avalonia/desktop) are products, not composed libraries; they additionally enforce compatibility at **runtime** via the `agentReady` `protocolVersion` handshake (ADR-011 D6), which survives unchanged as the process-to-process gate. Frontends version on the same `X.Y.Z` scheme as everything else.

7. **The skew-guard is retained, relaxed to patch only.** `Directory.Build.props` continues to error any packable project whose `Major.Minor` ≠ `ProtocolVersion.Current` (Decision 2's enforcement); it imposes no constraint on `Z`. A stale package cannot slip out of a cycle at the wrong `Major.Minor`.

8. **Tooling: per-package patch tags + a single protocol source of truth.** Mid-cycle patch releases are driven by per-package MinVer tag prefixes (e.g. `providers/anthropic-v0.2.5`); the `0.2` segment is pinned-to-protocol by the skew-guard. A cycle bump is: move `ProtocolVersion.Current` to the new `X.Y` and tag the whole set at `X.Y.0`. This per-package tag-prefix mechanism is the same trigger model the monorepo's path-filtered CI uses for per-area build/test — releases and CI share one mechanism. (Exact patch-derivation mechanics — MinVer commit-height vs explicit per-package tags — are an implementation detail of the monorepo change.)

9. **Third-party packages are unchanged from ADR-023 D5.** They pin `Dmon.Abstractions@X.Y.*` and version on their own cadence. Adopting the same `Major.Minor` = protocol convention is *recommended* (so their `@X.Y.*` packages slot cleanly into a cycle) but not enforced.

## Consequences

- **Per-package cadence within a cycle without losing the one-number pin.** A hot fix to one provider ships as a patch; unchanged packages stay put; authors still pin `@X.Y.*` everywhere. This is the property strict lockstep could not give and full independence gave only by sacrificing the coherent-graph guarantee.
- **The monorepo's per-area release/CI story has a versioning model that fits it.** Per-area tags map to per-package patch releases; the cycle wave maps to a coordinated `Everything.slnx` release.
- **Breaking changes are batched to cycle boundaries.** A package cannot be cleaned up in a breaking way mid-cycle; it waits for or forces a protocol `Minor` bump. Accepted as a feature: it gives consumers one breaking cadence to track.
- **Cycle boundaries republish unchanged packages.** Each `Major.Minor` bump produces an `X.Y.0` of every first-party package, some functionally identical to their predecessor but for the version. This is the cost of the universal `@X.Y.*` guarantee; it is mechanical (one wave) rather than ongoing churn.
- **Frontends with non-NuGet artifacts (Gateway daemon, dmonium `.app`) still participate in the cycle.** Their `X.Y.0` is a packaging/publish event, not a `dotnet nuget push` (see Open Question C).

## Alternatives

- **Strict three-part lockstep (ADR-011 D5 / ADR-023 D5 as written).** Rejected for the monorepo: every change re-releases the whole set; no per-package cadence; fights per-area CI/release and worktree parallelism. This ADR amends it rather than discarding it — `Major.Minor` lockstep is kept.
- **Fully independent SemVer per first-party package** (each pinning `Dmon.Abstractions@X.Y.*`, third-party style). Rejected: dissolves the `@X.Y.*` one-pin UX, moves the restore-time compatibility guarantee from "matching numbers" to per-package dependency ranges (more precise but more bookkeeping), and creates an N-dimensional compatibility matrix. It does buy honest per-package `Major`/`Minor` SemVer, which Decision 4 deliberately trades away for the coherent-graph guarantee.
- **Lockstep `Major.Minor` + a curated meta/bundle package for the one-pin path.** Considered while pure independence was on the table; rendered unnecessary by Decision 5 — shared `Major.Minor` restores the one-pin path natively, so a bundle would be redundant ceremony. (A curated bundle may still be wanted for *other* reasons; out of scope here.)

## Open Questions

- **A. Patch-derivation mechanics.** Whether `Z` derives from MinVer commit-height since the cycle tag, or from explicit per-package patch tags. Deferred to the monorepo change; does not affect the model.
- **B. No-op cycle releases.** Whether a package unchanged since the previous cycle must still cut a fresh `X.Y.0` artifact, or may alias/re-tag. Decide during implementation; Decision 2's *intent* is that `@X.Y.*` always resolves a complete set.
- **C. Cycle waves over heterogeneous frontend artifacts.** How the `X.Y.0` wave is expressed for non-NuGet frontends (Gateway framework-dependent publish, dmonium `.app`/`.dmg`, future Avalonia bundles) — tied to the monorepo release-matrix design.
- **D. Protocol cadence pressure.** Binding breaking changes to cycle boundaries (Decision 4) may pressure the protocol `Minor` to bump more often than the wire actually changes. Watch whether "we need a cycle to land a breaking package change" starts driving protocol versioning; revisit if it does.

## Relationship to other ADRs

- **ADR-011** — its 3-part protocol-keyed scheme (D5) is amended: `Major.Minor` stays protocol-keyed and lockstep; `Z` is freed per package. The `agentReady` `protocolVersion` gate (D6) is unchanged and becomes the explicit runtime compatibility point for frontend host apps (Decision 6). Cadence-independence (D8/D9) is realised at the patch level.
- **ADR-023** — its lockstep rule (D5) is amended exactly as ADR-011 D5; its granular-package topology (D2/D3), one-namespace verbs (D4), and prebuilt stock default (D8) are unchanged. The bundle-package idea floated during exploration is explicitly *not* adopted (Decision 5, Alternatives).
- **ADR-019** — file-based-program composition and `#:package` restore are unchanged; this ADR only changes how the pinned packages are versioned.
- **Forthcoming monorepo change** — supplies the top-level structure (`core/`/`providers/`/`tools/`/`middleware/`/`frontends/`, per-area `.slnx` + `Everything.slnx`), path-filtered CI, worktree workflow, and release matrix that consume this versioning model. This ADR is the versioning prerequisite settled ahead of that work.
