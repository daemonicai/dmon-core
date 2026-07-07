# ADR-035: Release Matrix — Resolving the Protocol-Cycle Versioning Open Questions

**Date:** 2026-07-07
**Status:** Accepted
**Amends:** ADR-024 (resolves Open Questions A/B/C; concretizes the per-package release trigger of Decision 8), ADR-025 (resolves the release-relevant part of Open Question E; concretizes the two-family release matrix of Decision 10), ADR-023 (fills the undefined publish status of `memory/Dmon.Memory`)
**Builds on:** ADR-011 (distribution model, `agentReady` protocol gate), ADR-023 (granular implementation packages), ADR-024 (protocol-cycle versioning), ADR-025 (monorepo topology, path-filtered CI), ADR-026 (memory bucket), ADR-033 (`ndmon` app-artifact framing)

## Context

ADR-024 established protocol-cycle versioning (`X.Y` = wire protocol, lockstep across the first-party set; `Z` = per-package patch) and ADR-025 established the monorepo's path-filtered CI ("core ⇒ all") and a two-family release matrix (NuGet packages vs app artifacts). Both left the *mechanics* of the release pipeline as explicit Open Questions:

- **ADR-024 OQ-A** — patch derivation: MinVer commit-height vs explicit per-package tags.
- **ADR-024 OQ-B** — must an unchanged package cut a fresh `X.Y.0` at a cycle boundary, or may it alias/re-tag?
- **ADR-024 OQ-C** / **ADR-025 OQ-E** — how do non-NuGet artifacts (the `Dmon.Network`/`ndmon` host, the dmonium `.app`/`.dmg`, the Avalonia `Dmon.Desktop` bundle) express a cycle wave?
- Two smaller undefined points surfaced by an audit of the actual tree: **`ndmon`/`Dmon.Network` family placement** (it is a `PackAsTool` NuGet package *and* framed as an app-artifact host in ADR-033/ADR-025), and **`memory/Dmon.Memory`** carries neither `IsPackable` nor `PackageId`, so its publish status is undefined.

The release pipeline cannot be written as OpenSpec tasks while these are open — every release job would encode an un-decided policy. The current `release.yml` covers only `sdk-*`/`dmon-*`/`core-*` tags — **4 of ~17 packable projects** — and its tag scheme does not match ADR-024's per-package-prefix intent. This ADR resolves the Open Questions so the forthcoming `release-matrix` change has a settled contract to implement. It records **policy only**; the workflow YAML, Makefile targets, and area-glob details belong to that change's design.

The audit context: AUDIT.md issue 10 (Medium) — "ADR-024/025 CI promises unimplemented." The path-filtered-CI half is unblocked (ADR-025 D9 is already decided) and is being implemented by the separate `ci-hardening` change; this ADR unblocks only the release-matrix half.

## Decision

### D1 — Patch (`Z`) is derived from explicit per-package tags, not commit height (resolves ADR-024 OQ-A)

Each first-party package's patch version is driven by an explicit git tag with a **per-package prefix**, consumed by **MinVer** (`fetch-depth: 0` is already in `release.yml`). Commit-height auto-patch is **rejected** for this monorepo: commit height is repo-wide, so *any* commit would bump *every* package's derived patch, directly violating ADR-024's rule that an unchanged package keeps `X.Y.0` within a cycle. Explicit per-package tags give each package an independent `Z` lineage while sharing the cycle's `X.Y`.

**Tag scheme:** `<area>/<name>-v<X.Y.Z>` — e.g. `providers/anthropic-v0.2.5`, `tools/dmail-v0.2.1`, `core/dmoncore-v0.2.3`, `frontends/dmon-v0.2.0`. The current `sdk-*`/`dmon-*`/`core-*` tag lines are **retired** (clean break — no production consumers; ADR project policy).

### D2 — A cycle boundary re-releases the whole set at `X.Y.0`; no-op releases are real (resolves ADR-024 OQ-B)

Opening a protocol cycle (`ProtocolVersion.Current` moves to a new `X.Y`) tags **every** first-party NuGet package at `X.Y.0` in one scripted wave — **including unchanged packages**. Aliasing/re-tagging an old build is **rejected**: ADR-024's `@X.Y.*` one-pin guarantee requires every package to have a real, restorable `X.Y.0` in the current cycle, or a `Dmon.cs` pinning `@0.3.*` would fail to restore any package that never published in the `0.3` cycle. A no-op release is therefore a genuine fresh `X.Y.0` package. Mid-cycle, only packages that ship a fix cut a new `X.Y.Z` (Z≥1) on their own tag.

### D3 — Two release families, keyed by publish sink (resolves ADR-024 OQ-C / ADR-025 OQ-E, release-relevant part; concretizes ADR-025 D10)

Membership is decided by **where the artifact is published**, not by what kind of app it is:

- **NuGet family** — publishes to nuget.org via `dotnet nuget push`. Members: all `core/` contracts + engine, all `providers/`, all `tools/`, `memory/` backends, and the two **`PackAsTool` dotnet tools** (`dmon` = `frontends/Dmon.Terminal`, `ndmon` = `frontends/Dmon.Network`). Subject to `X.Y` protocol lockstep; a cycle boundary re-releases all of them (D2).
- **App-artifact family** — publishes as a downloadable bundle attached to a **GitHub Release** (not nuget.org). Members: the dmonium macOS app (`daemon/Daemon.App`, `.app`/`.dmg`), the `Dmon.Desktop` Avalonia bundle, and any future self-contained bundled host. Each carries an `X.Y.Z` version and enforces protocol compatibility **at runtime** via the `agentReady` `protocolVersion` handshake (ADR-011 D6), not at restore time. App artifacts version on their own tag prefixes (`app/<name>-v<X.Y.Z>`) and are **not** required to move in lockstep with a NuGet cycle, matching ADR-024's existing app-artifact carve-out and ADR-033 D2.

### D4 — `ndmon`/`Dmon.Network` is NuGet family, not app-artifact (reconciles ADR-025 D10 / ADR-033)

`Dmon.Network` ships today as `PackAsTool=true`, installed via `dotnet tool install -g Dmon.Network` → `~/.dotnet/tools/ndmon` (ADR-033). A dotnet tool publishes to nuget.org, so by D3 it is **NuGet family** and moves with the protocol cycle — exactly like the `dmon` terminal tool. ADR-025 D10's "Gateway daemon publish" phrasing (written before ADR-033 made the host a `PackAsTool`) is reconciled to this: the *tool package* is NuGet family. Only a hypothetical **self-contained bundled** network host (bundling its own core, currently deferred) would be app-artifact. ADR-033 D2's "independently-versioned app artifact, exempt from lockstep" is **narrowed** by this ADR: as a NuGet dotnet tool, `Dmon.Network` is on the protocol-lockstep train for its `X.Y`; its independence is limited to `Z` like every other NuGet-family package.

### D5 — `memory/Dmon.Memory` is packable, NuGet family (fills the ADR-023 gap)

`memory/Dmon.Memory` (the sqlite-vec short-term backend, ADR-026) is made **packable** with an explicit **`PackageId = Dmon.Memory`**, sibling to the already-packable `Dmon.Memory.Meko`. Its current absence of `IsPackable`/`PackageId` is a latent defect (the SDK would default it to packable with an assembly-name id, unversioned by the release train). It joins the NuGet family and the `X.Y` lockstep.

### D6 — One canonical area map is shared by path-filtered CI and the release matrix (concretizes ADR-024 D8)

ADR-024 D8's "releases and path-filtered CI share one trigger mechanism" is made concrete as a single **area→paths** map that is the source of truth for both:

- **CI** (on PR/push): a change under an area's paths builds/tests that area's `.slnx`; a change under `core/**` or root build config (`Directory.*.props`, `*.slnx`, `.github/`, `Makefile`) is **upstream of everything ⇒ build/test `Everything.slnx`** (ADR-025 D9). The macOS Swift job for `daemon/Daemon.App/**` is orthogonal (it links no .NET ProjectReference; it is a wire client) and triggers only on its own path.
- **Release** (on tag): a `<area>/<name>-v…` tag maps, via the same area map, to the project(s) that area publishes.

The map is derived from the **ProjectReference DAG**, whose shared root is `core/`. The authoritative per-area path globs and the reconciliation of ADR-025 D2's stale enumeration (the tree has 7 providers incl. `Mtplx`/`Mlx`, and a `memory/` bucket — not the "Omlx, LlamaCpp / middleware/" the ADR text lists) are an implementation detail of the `ci-hardening` and `release-matrix` change designs, not fixed numerically here.

### D7 — Package → family map (the deliverable the release-matrix change implements)

| Package | Bucket | Family | Tag prefix |
|---|---|---|---|
| `Dmon.Protocol`, `Dmon.Abstractions` | core | NuGet (contracts) | `core/<name>-v` |
| `dmoncore` (`Dmon.Core`) | core | NuGet | `core/dmoncore-v` |
| `Dmon.Providers.{Anthropic,OpenAI,Gemini,Ollama,LlamaCpp,Mtplx,Mlx}` | providers | NuGet | `providers/<name>-v` |
| `Dmon.Tools.{Builtin,WebSearch,Dcal,Dmail}` | tools | NuGet | `tools/<name>-v` |
| `Dmon.Memory`, `Dmon.Memory.Meko` | memory | NuGet (D5) | `memory/<name>-v` |
| `dmon` (`Dmon.Terminal`, PackAsTool) | frontends | NuGet tool | `frontends/dmon-v` |
| `ndmon` (`Dmon.Network`, PackAsTool) | frontends | NuGet tool (D4) | `frontends/ndmon-v` |
| dmonium (`Daemon.App`, `.app`/`.dmg`) | daemon | app-artifact (D3) | `app/dmonium-v` |
| `Dmon.Desktop` (Avalonia bundle) | frontends | app-artifact (D3) | `app/desktop-v` |

**Not published:** `Dmon.Runtime`, `Dmon.Protocol.SchemaGen`, `Daemon.Routing`, `services/Dcal`, `services/Dmail` (standalone servers — app artifacts *iff/when* a deploy story is defined; out of scope here), and all test/sample projects.

## Consequences

- The `release-matrix` OpenSpec change now has a settled contract: per-package MinVer tag prefixes (D1), a cycle-wave script (D2), two publish sinks (D3), and a concrete package→family table (D7). It can be proposed without stopping on an unresolved Open Question.
- `release.yml`'s coverage gap (4/17 packages) and its non-conforming tag scheme are addressed by that change, not this ADR.
- `memory/Dmon.Memory` gains `IsPackable`/`PackageId` (D5) — a one-line project edit that the release-matrix change carries.
- The **skew-guard** (ADR-024 D7: `Directory.Build.props` errors any packable project whose `Major.Minor` ≠ `ProtocolVersion.Current`) is assumed by D1/D2; whether it is already present or must be added is left to the release-matrix change to verify.
- No wire-protocol change. No change to `ci-hardening` (which implements only the already-decided path-filter + quick wins + hygiene).

## Alternatives

- **MinVer commit-height auto-patch (no per-package tags).** Rejected in D1: repo-wide commit height breaks per-package independent `Z` in a monorepo.
- **A meta/bundle package that pins the whole set.** Already rejected by ADR-024 D5 (`@X.Y.*` one-pin obviates it); nothing here revisits that.
- **Alias unchanged packages at a cycle boundary instead of re-releasing.** Rejected in D2: breaks restore-time resolution of `@X.Y.*`.
- **Treat `ndmon` as app-artifact (independent versioning).** Rejected in D4: it is a nuget.org dotnet tool; app-artifact status is reserved for self-contained bundles.
- **Fold these decisions into the release-matrix change's `design.md` instead of an ADR.** Rejected: versioning/release-family policy is maximally cross-cutting and permanent — it governs every package's version forever — which is ADR material, not change-local design. Resolving standing Open Questions in the ADR line keeps the binding record coherent.

## Open Questions

- **ADR-024 OQ-D (protocol-cadence pressure)** — whether tying all breaking changes to a protocol cycle over-couples release cadence — remains open and is **not** addressed here; it is a cadence-policy concern, not a pipeline blocker.
- **`services/Dcal` / `services/Dmail` server deployment** — these standalone servers have no publish story; whether they become app artifacts (containers? GitHub Release binaries?) is deferred until a deploy target is chosen.
- **App-artifact signing/notarization** (macOS `.app`/`.dmg` Gatekeeper) — a real-world requirement for the dmonium artifact, deferred to the release-matrix change or a follow-up.
