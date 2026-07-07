## Context

`release.yml` today: triggers on `sdk-*`/`dmon-*`/`core-*` tags, runs `make build`+`make test`, then a `case "$TAG"` packs a hardcoded subset (`sdk` → Protocol+Abstractions; `dmon` → Terminal; `core` → Dmon.Core) and `dotnet nuget push`es to nuget.org. It covers 4 of ~17 packable projects. The repo already uses MinVer (`fetch-depth: 0`).

ADR-035 settles the contract this change implements: per-package tags (D1), cycle-wave (D2), two families by sink (D3), `ndmon` on the lockstep (D4), `Dmon.Memory` packable (D5), a shared area map (D6), and a concrete package→family→tag-prefix table (D7). The `package-publishing` standing spec is partly stale against D4 (it marks `Dmon.Network` exempt from the protocol gate) and omits memory packages from the packable set.

## Goals / Non-Goals

**Goals:**
- Every NuGet-family package (ADR-035 D7 table) has a working per-package release path.
- A protocol-cycle boundary re-releases the whole NuGet set at `X.Y.0` reproducibly.
- App artifacts (dmonium, Desktop) have a GitHub-Release publish path distinct from nuget.org.
- `Dmon.Memory` is packable; the standing spec matches ADR-035 (ndmon on lockstep; memory in the set).

**Non-Goals:**
- CI path-filter / `ci.yml` (change `ci-hardening`).
- `services/Dcal`/`Dmail` server deployment (ADR-035 Open Question).
- macOS code-signing/notarization of the `.app`/`.dmg` (may scaffold the job; signing deferred).
- Reworking `ProtocolVersion` or the wire protocol.

## Decisions

**D1 — MinVer per-package tag prefixes drive `Z`.** Each package's `.csproj` (or a per-area `Directory.Build.props`) sets `MinVerTagPrefix` to its ADR-035 D7 prefix (e.g. `providers/anthropic-v`). A tag `providers/anthropic-v0.2.5` releases exactly that package at `0.2.5`. `release.yml`'s trigger becomes the set of `*/**-v*` prefixes (or a single glob with a dispatch step that maps the tag to its project via the shared area map). The `case "$TAG"` subset logic is replaced by map-driven selection.

**D2 — Cycle-wave is a scripted, reproducible tagging step.** A helper (`scripts/release-wave.sh` / `make release-wave X.Y`) tags every NuGet-family package at `<prefix>X.Y.0` in one pass. Guarded by the ADR-024 D7 skew-check so it can only run when `ProtocolVersion.Current == X.Y`. This satisfies ADR-035 D2 (unchanged packages get a real `X.Y.0`). The workflow itself stays per-tag; the wave just pushes many tags.

**D3 — Two jobs by sink, not by app type.** `release.yml` gets (a) a **nuget** job triggered by `<area>/<name>-v*` tags → `dotnet pack --no-build` the mapped project(s) → `dotnet nuget push` (unchanged auth/secret). (b) an **app-artifact** job triggered by `app/<name>-v*` tags → build the bundle (dmonium via `make daemon-app` packaging; Desktop via `dotnet publish`) → attach to a GitHub Release. The nuget job is the existing pipeline generalized; the app-artifact job is new. Keep the `.snupkg` symbol push (sdk line behaviour) generalized to any package with symbols.

**D4 — `ndmon`/`dmon` tools pack in the nuget job on the protocol line.** Per ADR-035 D4, the two `PackAsTool` packages are ordinary NuGet-family members: mapped by their tag prefixes (`frontends/dmon-v`, `frontends/ndmon-v`), packed and pushed like any other. No special-casing; drop the "exempt" handling. `make network`'s hardcoded `--version 0.1.0` is removed in favour of MinVer.

**D5 — `Dmon.Memory` becomes packable (one-line project edit).** Add `<IsPackable>true</IsPackable>` and `<PackageId>Dmon.Memory</PackageId>` to `memory/Dmon.Memory/Dmon.Memory.csproj`, plus whatever central metadata the other packages inherit from `Directory.Build.props`. Add its `MinVerTagPrefix` (`memory/memory-v` or `memory/Dmon.Memory-v` — pick to match the D7 table's `<name>` convention).

**D6 — The area→paths map is the single artifact shared with `ci-hardening`.** Do not duplicate it. If `ci-hardening` landed the map as a JSON/YAML block, `release.yml` reads the same file to map a tag's `<area>/<name>` to its project path(s). If `ci-hardening` kept it inline in `ci.yml`, extract it to a shared location as part of this change and point both workflows at it. Either way there is exactly one map (ADR-035 D6).

**D7 — Verify/add the ADR-024 D7 skew-guard.** Confirm `Directory.Build.props` errors any packable project whose `Major.Minor` ≠ `ProtocolVersion.Current`. If absent, add it — the cycle-wave (D2) and per-package releases (D1) both rely on it to prevent a mis-tagged package escaping to nuget.org.

## Risks / Trade-offs

- **[A cycle wave mis-tags or double-publishes]** → `dotnet nuget push --skip-duplicate` (already used) makes re-runs idempotent; the skew-guard (D7) blocks wrong-`X.Y` packs; the wave script is reproducible and reviewable before tags are pushed.
- **[App-artifact job without signing ships an unnotarized `.app`]** → Scope the first cut to producing the artifact and attaching it; gate distribution on a follow-up that adds signing (ADR-035 Open Question). Clearly label unsigned artifacts.
- **[Shared map drifts between the two workflows]** → D6 mandates a single file; a test/lint can assert both workflows reference it. If `ci-hardening` hasn't extracted it, this change does.
- **[MinVer tag-prefix collisions]** (e.g. `frontends/dmon-v` is a prefix of nothing else, but `core/dmon…`?) → Choose unambiguous prefixes in the D7 table; verify no prefix is a prefix of another that would mis-resolve.
- **[BREAKING spec change on ndmon]** → The `package-publishing` "app-artifact tools exempt" scenario is reversed. No production consumers depend on ndmon's version line (no production deployments policy), so the break is safe; the spec delta records it.

## Migration Plan

Clean break on the tag scheme (no production consumers). Old `sdk-*`/`dmon-*`/`core-*` tags remain in history but are no longer triggers. First release under the new scheme is a full cycle wave at the current `ProtocolVersion.Current`. Rollback: revert the workflow/project commits; the old `release.yml` tag lines can be restored from history if ever needed.

## Open Questions

- Carried from ADR-035: `services/Dcal`/`Dmail` server deploy story; macOS signing/notarization; ADR-024 OQ-D cadence pressure. None blocks the NuGet-family matrix; the app-artifact job may ship unsigned first.
- Does `Dmon.Desktop` (Avalonia) have a working `dotnet publish` bundle recipe today, or does the app-artifact job need to define one? Investigate at implementation; if it's non-trivial, scope Desktop to a follow-up and ship dmonium + the full NuGet family first.
