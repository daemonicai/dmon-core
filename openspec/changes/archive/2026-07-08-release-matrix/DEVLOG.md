# DEVLOG — release-matrix

Cross-block memory for the architect. Newest block last.

## Pinned facts (read every block)

- **Change scope:** implement ADR-035 (all decisions) — build the full granular release matrix. Lands after `ci-hardening` (merged; created the shared area map) and after ADR-035 (accepted).
- **Hard constraint:** this change must **NOT touch `.github/workflows/ci.yml`** (proposal). It rewrites `.github/workflows/release.yml` and adds project/Makefile metadata.
- **Shared area map** already exists at `.github/area-map.yml` (from `ci-hardening`); it is **consumed** by `release.yml` in Section 3, not edited here (task 2.1).
- **`ProtocolVersion.Current == 0.2`**; `MinVerMinimumMajorMinor == 0.2`. Do not change these. Untagged local packs → MinVer prerelease `0.2.0-alpha.0.N`.
- **Skew-guard** = target `CheckProtocolVersionSkew` in root `Directory.Build.props` (`AfterTargets="MinVer"`, condition `IsPackable==true and IsProtocolKeyedPackage != 'false'`). Already present and covers all packable projects.
- **Human-in-the-loop tasks (stop-and-ask when reached):** 6.3 (push real tags to nuget.org + `app/*` to a GitHub Release — needs `NUGET_API_KEY` + maintainer). Produce the copy-paste recipe, then WAIT for maintainer confirmation before ticking.
- **Deferred scope (ADR-035 Open Questions):** app-artifact signing/notarization (5.1 ships dmonium unsigned+labelled); Desktop `dotnet publish` bundle (5.2 — defer to follow-up if non-trivial, log it); `services/Dcal`/`Dmail` server deploy story.

## The canonical `MinVerTagPrefix` map (set in Block 1 — Section 3 consumes these)

Every NuGet-family packable project → its exact tag prefix. **No prefix is a string-prefix of another** (Section 3 tag→project resolution depends on this):

| Project | PackageId | Prefix |
|---|---|---|
| core/Dmon.Protocol | Dmon.Protocol | `core/protocol-v` |
| core/Dmon.Abstractions | Dmon.Abstractions | `core/abstractions-v` |
| core/Dmon.Core | dmoncore | `core/dmoncore-v` |
| providers/Dmon.Providers.Anthropic | … | `providers/anthropic-v` |
| providers/Dmon.Providers.OpenAI | … | `providers/openai-v` |
| providers/Dmon.Providers.Gemini | … | `providers/gemini-v` |
| providers/Dmon.Providers.Ollama | … | `providers/ollama-v` |
| providers/Dmon.Providers.LlamaCpp | … | `providers/llamacpp-v` |
| providers/Dmon.Providers.Mtplx | … | `providers/mtplx-v` |
| providers/Dmon.Providers.Mlx | … | `providers/mlx-v` |
| tools/Dmon.Tools.Builtin | … | `tools/builtin-v` |
| tools/Dmon.Tools.WebSearch | … | `tools/websearch-v` |
| tools/Dmon.Tools.Dcal | … | `tools/dcal-v` |
| tools/Dmon.Tools.Dmail | … | `tools/dmail-v` |
| memory/Dmon.Memory | Dmon.Memory | `memory/memory-v` |
| memory/Dmon.Memory.Meko | Dmon.Memory.Meko | `memory/meko-v` |
| frontends/Dmon.Terminal | (dmon tool) | `frontends/dmon-v` |
| frontends/Dmon.Network | Dmon.Network (ndmon tool) | `frontends/ndmon-v` |

`samples/Dmon.SampleExtension` (`sample-`) is **NOT** NuGet-family (ADR-035 D7 "not published") — excluded, version-injected via `pack-core.sh -p:MinVerVersionOverride`. Reviewer flagged: confirm in a later change whether the sample stays deliberately excluded from the D7 scheme (non-blocking).

## Block 1 — tasks 1.1–1.4 (DONE, committed)

Version-metadata prerequisites (ADR-035 D4/D5/D7).

- **1.1** `memory/Dmon.Memory/Dmon.Memory.csproj` made packable: `IsPackable=true`, `PackageId=Dmon.Memory`, `MinVerTagPrefix=memory/memory-v`, one-line `Description`. Mirrors `Dmon.Memory.Meko`; central metadata inherited from root props (no duplication); no `PackageReadmeFile` (packs fine without).
- **1.2** Skew-guard verify-only — root `Directory.Build.props` **unchanged** (correct outcome). Guard already covers the newly-packable `Dmon.Memory` and un-exempted `Dmon.Network`.
- **1.3** All 18 NuGet-family `.csproj` set to the D7 prefixes above. **`Dmon.Network` un-exempted (D4):** removed `IsProtocolKeyedPackage=false` + `MinVerVersionOverride=0.1.0`, added `frontends/ndmon-v`, rewrote stale "exempt/independently-versioned" comments to the ADR-035 D4 framing (NuGet-family dotnet tool on the protocol lockstep). Decision: full per-project prefix can't be factored into per-area props (each `<name>` differs) → set per-`.csproj`, no per-area props added.
- **1.4** `make network`: `--version 0.1.0` → `--prerelease` (untagged pack is now a MinVer prerelease; `dotnet tool install` skips prereleases without the flag).
- **Gates:** `make build` clean (0 warn); `env -u MEKO_API_KEY make test` all 20 assemblies pass; `openspec validate release-matrix --strict` valid; `make network` live-verified installing `Dmon.Network 0.2.0-alpha.0.259`.
- **Reviewer:** SIGN-OFF, no nits.
- **Note:** the `coreVersion:"0.1.0"` strings in `test/Dmon.Network.Tests` / `test/Dmon.Runtime.Tests` are **agentReady handshake fixtures, NOT package versions** — left untouched (do not "fix" them in later blocks).

## Block 2 — tasks 2.1, 3.1, 3.2, 3.3 (DONE, committed)

Rewrote `.github/workflows/release.yml` to per-package tag-driven NuGet publishing (ADR-035 D1/D3/D6/D7).

- **3.1** Trigger → 5 NuGet-family area globs (`core/*-v*`, `providers/*-v*`, `tools/*-v*`, `memory/*-v*`, `frontends/*-v*`). All require a `/` (GH Actions `*` doesn't cross `/`), so legacy `sdk-*`/`dmon-*`/`core-*` can't match and are retired. Deliberately using 5 explicit globs (not a blanket `*/**-v*`) **excludes `app/**`/`samples/**` from the NuGet job** — matches the design-D3 family split.
- **3.2** Replaced `case "$TAG"` with `MinVerTagPrefix`-driven resolution: `TAG=${{ github.ref_name }}` (preserves slash) → `PREFIX="${TAG%-v*}-v"` → `grep -rlF --include='*.csproj' "<MinVerTagPrefix>${PREFIX}</MinVerTagPrefix>"` (exact-element fixed-string match) → guard `COUNT -ne 1` fails loudly → **`dotnet build "$PROJ" -c Release` THEN `dotnet pack --no-build`**. Kept the nuget.org push + `--skip-duplicate` + `.snupkg` conditional verbatim.
  - **Critical finding (why the explicit build):** `make build` = `build-core build-terminal build-memory` only — it does **NOT** compile providers/tools/`Dmon.Network`/`Dmon.Memory.Meko`. A bare `--no-build` pack on a provider tag would fail. The explicit `dotnet build "$PROJ"` (same `-c Release`) fixes this; `--no-build` pack then honors design-D3 (pack doesn't rebuild).
- **2.1 — deliberate non-consumption (architectural decision, agreed by architect + reviewer):** task 2.1's literal "point release.yml at the [area] map" is satisfied by **deliberately NOT** consuming `.github/area-map.yml`. That map is **area-granular** (`dorny/paths-filter` config) and cannot mechanically derive `<name>`→`.csproj` (`anthropic`→`Dmon.Providers.Anthropic`). Duplicating a tag→project map would **violate ADR-035 D6** ("exactly one map"). So the **single source of tag→project truth is each project's own `<MinVerTagPrefix>`** (set in Block 1). `area-map.yml` stays the shared *area* map for CI (untouched; `ci.yml` refs it ~line 49). A header comment in `release.yml` documents this. **Future architects: this is why release.yml does not read area-map.yml.**
- **3.3** All 18 NuGet-family prefixes resolve to exactly one `.csproj`; `app/dmonium-v` and `sample-v` → 0 (correctly excluded).
- **Gates:** `make build` clean; `env -u MEKO_API_KEY make test` all green (Dmon.Core.Tests 610 + all others); `openspec validate release-matrix --strict` valid; YAML parses (Ruby loader — `actionlint`/`yq` not in env); 18/18 resolution dry-run; provider build-then-pack dry-run produced `.nupkg`+`.snupkg`.
- **Reviewer:** SIGN-OFF. One nit fixed by orchestrator: removed the now-dead `id: pack` step attribute (nothing consumed `steps.pack.outputs` after the `case` removal).
- **Only `release.yml` changed** — `ci.yml`, `area-map.yml`, all `.csproj`, `Directory.Build.props` untouched.

## Block 3 — task 4.1 (DONE, committed)

Cycle-wave tagging helper (ADR-035 D2 / design D2).

- **New `scripts/release-wave.sh`** — takes `X.Y`; order is arg-validate → **skew-guard** (parses `ProtocolVersion.Current` from `core/Dmon.Protocol/ProtocolVersion.cs`, exits non-zero if `X.Y` ≠ it) → **discover** the 18 prefixes by `grep -rhoE '<MinVerTagPrefix>…</MinVerTagPrefix>'` scoped to `core/ providers/ tools/ memory/ frontends/` (single-source per ADR-035 D6; the literal-18 is only a `EXPECTED_COUNT` cross-check assertion) → emit `<prefix>X.Y.0` → push. **Dry-run by default**; real `git tag`/`git push` strictly behind `--push`/`PUSH=1`.
- **Makefile** — thin `make release-wave VERSION=X.Y [PUSH=1]` wrapper (`--push` passed only on `PUSH=1` via `$(if $(filter 1,$(PUSH)),--push,)`), `release-wave` added to `.PHONY`. All logic in the script.
- **Verified:** `release-wave.sh 0.2` → exactly 18 `-v0.2.0` tags, `git tag` count 3→3 (no pollution); `0.3` → exit 1 before any emission; missing/malformed arg → exit 1 + usage; `bash -n` parses.
- **Gates:** `make build` clean; `env -u MEKO_API_KEY make test` all green; `openspec validate release-matrix --strict` valid.
- **Reviewer:** SIGN-OFF. 3 non-blocking nits left as-is (worker discretion): `\s` in `grep -E` (GNU ext; `[[:space:]]` is POSIX), `mapfile` needs bash≥4 (fine via `env bash`/Linux CI; **note for a maintainer running the wave on stock macOS `/bin/bash` 3.2 — use Homebrew bash**), redundant `shift || true`.
- **Only `scripts/release-wave.sh` (new) + `Makefile` changed.**

## Block 4 — tasks 5.1, 5.2 (DONE, committed)

App-artifact family (ADR-035 D3 / design D3). Only `.github/workflows/release.yml` changed.

- **5.1** Broadened the release.yml trigger with `app/*-v*` (now 6 globs). Guarded the existing nuget `release` job `if: ${{ !startsWith(github.ref_name, 'app/') }}` (else an `app/*` tag makes its `MinVerTagPrefix` grep resolve 0 projects → red-fail). Added a new `app-artifact` job: `runs-on: macos-latest`, `if: ${{ startsWith(github.ref_name, 'app/') }}`, **job-level** `permissions: contents: write` (top-level stays `contents: read` — least-privilege). Steps: checkout (no `fetch-depth: 0`) → tag `case` guard (accept `app/dmonium-v*`; **fail-fast** `app/desktop-v*` deferral; fail unknown) → `VERSION=${TAG##*-v}` (from the tag, not MinVer — ADR-024 app-family independent versioning) → `make daemon-app` → wrap `daemon/Daemon.App/.build/release/DaemonApp` in an unsigned `dist/dmonium.app` (Info.plist `plutil -lint`ed, `CFBundleIdentifier=ai.daemonic.dmonium`) → `ditto -c -k --keepParent` → `dmonium-<version>-macos-arm64-unsigned.zip` → `gh release create "$TAG"` (title/notes say **unsigned**, Gatekeeper-quarantine note) via auto-provisioned `secrets.GITHUB_TOKEN`. The two jobs are mutually-exclusive + exhaustive over the trigger union, routed purely by `ref_name` prefix.
- **5.2 — DEFERRED + logged (verbatim):** *`Dmon.Desktop` bundling is deferred. No working `dotnet publish` bundle recipe exists today: `frontends/Dmon.Desktop/Dmon.Desktop.csproj` is `IsPackable=false`, plain `OutputType=Exe`/`net10.0` with no `RuntimeIdentifier`, no self-contained/`PublishSingleFile`, no icon/bundle metadata; no Makefile target or publish script; Avalonia has no built-in `.app`/installer bundler. The Desktop `README.md` already marks the installable artifact an open question for a future change ("## Deferred: installable artifact"). Encoded in code — an `app/desktop-v*` tag fails fast in the `app-artifact` `case` guard ("...deferred to a follow-up change; Dmon.Desktop has no bundle recipe yet.") — plus a `release.yml` header comment. `app/desktop-v*` packaging NOT implemented.* **→ candidate follow-up change: Dmon.Desktop installable-artifact packaging.**
- **Local dry-run** (no publish): `make daemon-app` + wrapping shell produced a valid nested `dmonium.app`, `plutil -lint` passed, zip verified; cleaned up.
- **Gates:** `make build` clean; `env -u MEKO_API_KEY make test` all green; `openspec validate release-matrix --strict` valid; YAML parses (Ruby loader).
- **Reviewer:** SIGN-OFF, no blockers. 3 non-blocking nits left as-is (recorded for a possible polish follow-up): (1) "Build dmonium" step lacks explicit `set -euo pipefail` (harmless — single cmd; GH default shell is `-eo pipefail`); (2) `macos-latest`↔`arm64` filename coupling — pinning `macos-15` would make the arch label explicit if GitHub ever repoints the alias; (3) `gh release create` not idempotent — a same-tag re-run fails (ops-runbook note).

## Block 5 — tasks 6.1, 6.2 (DONE, committed) + 6.3 authored (PENDING maintainer)

Gates + release-path dry-runs, and a **pack-path remediation** that 6.2 forced.

- **6.1** All three gates pass: `make build` clean; `env -u MEKO_API_KEY make test` all 20 assemblies green; `openspec validate release-matrix --strict` valid.
- **6.2** Release-path dry-runs (no publish): all 18 NuGet-family projects `dotnet pack --no-build` cleanly; skew-guard **rejects** a deliberately mis-versioned pack (`dotnet pack core/Dmon.Protocol … -p:MinVerVersionOverride=9.9.0-alpha.0.1` → exit 1, no nupkg, `Directory.Build.props(96,5): error : Version-skew guard: package version Major.Minor is 9.9 but ProtocolVersion.Current is 0.2`); wave `0.2`→18 tags dry-run, `0.3`→exit 1; `release.yml` parses.
  - **IMPORTANT — the first 6.2 attempt caught two real pack defects in already-committed blocks** (Section 1 & 3). `make build` never *packs* providers/tools/frontends/`Dmon.Memory`, so these pack paths were unexercised until 6.2. Fixed here (release.yml deliberately UNchanged — `--no-build` kept for all 18, per user decision + design D3):
    - **`memory/Dmon.Memory` (NU5100/NU5118):** `LLamaSharp.Backend.Cpu` injects native DLLs as `<Content>` via an auto-imported build `.props`; with no RID, pack globs every RID's natives outside `lib/` → errors under `TreatWarningsAsErrors`. **Fix: `<IncludeContentInPack>false</IncludeContentInPack>`** in `Dmon.Memory.csproj`. The natives are its only Content (GGUF model is runtime-downloaded); consumers still get them transitively via the backend `.props`. `Dmon.Memory.Meko` was unaffected (no LLamaSharp ref).
    - **`frontends/Dmon.Terminal` (NETSDK1085):** its prebuilt-default-core staging targets drag a real P2P `Build` into the `--no-build` pack. **Fix: NoBuild-guard** — added `and '$(NoBuild)' != 'true'` to `CopyPrebuiltDefaultCoreToPublish`'s Condition AND nulled `CollectPrebuiltDefaultCore`'s `DependsOnTargets` (via an intermediate `$(_CollectPrebuiltDefaultCoreDependsOn)` property) under NoBuild. `StagePrebuiltDefaultCoreForPack` untouched → the dmoncore payload (93 entries under `tools/net10.0/any/dmoncore/`) still ships; the publish/`make build` path (NoBuild unset) is unchanged. `Dmon.Network` (also `PackAsTool`) needed nothing — it has no such staging targets.
    - **⚠️ LOAD-BEARING MSBUILD LESSON (for future architects/workers):** MSBuild evaluates a target's **`DependsOnTargets` BEFORE that target's own `Condition`**. So guarding only a target's `Condition` does NOT keep `ResolveReferences` (and its P2P `Build`) out of a `--no-build` pack when the target is *reached as a dependency* of a must-run target (here `StagePrebuiltDefaultCoreForPack` `BeforeTargets="GenerateNuspec"`). You must null the dependency **edge** itself. Any future `--no-build`-packable project that stages a prebuilt-file payload will hit this same trap.
- **6.3 — AUTHORED, NOT TICKED (human-in-the-loop / stop-and-ask).** Maintainer verification recipe appended to `openspec/changes/release-matrix/design.md` (`## Maintainer verification (task 6.3)`): NuGet single-tag receipt (`core/protocol-v0.2.0` → nuget.org, needs `NUGET_API_KEY`) + app-artifact single-tag receipt (`app/dmonium-v*` → GitHub Release, `gh release view`, Gatekeeper + non-idempotent caveats) + the bulk cycle-wave note. **Requires secrets absent from CI; the maintainer must run it and confirm before 6.3 is ticked.**
- **Reviewer:** SIGN-OFF, no blockers. Independently verified the Terminal payload survives (93 entries), Memory packs clean (lib present, no leaked natives), publish path unchanged, 6.3 recipe complete, scope clean, gates green.
- **Remediation touched only** `memory/Dmon.Memory/Dmon.Memory.csproj`, `frontends/Dmon.Terminal/Dmon.Terminal.csproj`, and `design.md` (the 6.3 recipe).

## Archive (2026-07-08) — 6.3 deferred to Trusted Publishing follow-up

PR #87 MERGED to main (merge `31e9f15`). At archive time the maintainer, going to create a `NUGET_API_KEY`, was steered by nuget.org to **Trusted Publishing** (OIDC, no long-lived key) — the recommended path. Decision: **do NOT create a throwaway API key** just to tick 6.3. Instead:
- **6.3 marked `[~]` (deferred), not `[x]`** — the recipe is authored in `design.md`; the live nuget.org receipt is intentionally performed under a **follow-up change `release-trusted-publishing`** whose first OIDC publish IS the receipt. All release machinery is dry-run-verified (6.2). 13/14 ticked + 6.3 deferred-documented.
- **Archived release-matrix FIRST (before authoring the follow-up)** for spec-delta hygiene: the follow-up amends the same `package-publishing` "Tag-driven release pipeline" requirement, so its delta must be authored against the standing spec AFTER release-matrix's delta syncs on archive (else two unarchived changes collide on one requirement).
- Follow-up `release-trusted-publishing`: rewire `release.yml` `release` job to `NuGet/login@v1` OIDC (`id-token: write`, temp key, `NUGET_USER` = nuget.org profile name) + amend the spec off `NUGET_API_KEY`. Maintainer-side prereqs (outside the repo): nuget.org Trusted Publishing policy (Owner `daemonicai`, Repo `dmon-core`, Workflow File `release.yml`) + `NUGET_USER` secret.
