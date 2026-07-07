## Why

`release.yml` publishes only the `sdk-*`/`dmon-*`/`core-*` tag lines — **4 of ~17 packable projects**. Every provider (7), every tool (4), `Dmon.Memory` / `Dmon.Memory.Meko`, and the `ndmon` tool have **no release path**, and the tag scheme doesn't match ADR-024's per-package-prefix intent (AUDIT.md issue 10, Medium). ADR-035 (just accepted) resolves the versioning Open Questions that blocked this, so the full granular release matrix can now be built to a settled contract. Two standing `package-publishing` requirements also currently contradict ADR-035 D4 (they mark `ndmon` "exempt from the protocol lockstep") and omit the memory packages from the packable set.

## What Changes

- **Per-package tag-driven releases** (ADR-035 D1): retire `sdk-*`/`dmon-*`/`core-*`; adopt `<area>/<name>-vX.Y.Z` tags consumed by MinVer, covering **all** NuGet-family packages (contracts, `dmoncore`, all providers, all tools, both memory backends, `dmon`, `ndmon`).
- **Cycle-wave release** (ADR-035 D2): a script/target that, at a protocol-cycle boundary, tags the whole NuGet-family set at `X.Y.0` (unchanged packages included, so `@X.Y.*` always restores).
- **Two release families keyed by publish sink** (ADR-035 D3): NuGet family → nuget.org; app-artifact family (dmonium `.app`/`.dmg`, `Dmon.Desktop` bundle) → GitHub Release attachments on `app/<name>-vX.Y.Z` tags.
- **`ndmon` reclassified onto the protocol lockstep** (ADR-035 D4): it is a nuget.org dotnet tool, so it shares the cycle `X.Y` (Z-only independence) — **BREAKING** vs the current spec's "exempt/independent" wording.
- **`Dmon.Memory` made packable** (ADR-035 D5): add `IsPackable`/`PackageId = Dmon.Memory`; include both memory backends in the packable set and the NuGet family.
- **Consume the shared area→paths map** (ADR-035 D6) that `ci-hardening` establishes, so releases and path-filtered CI agree on which paths belong to which package/area.

Sequencing: this change should land **after `ci-hardening`** (which creates the shared area map) and **after ADR-035** (accepted). It does not touch `ci.yml`.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `package-publishing`: modify **Tag-driven release pipeline** (per-package `<area>/<name>-v` tags, full package coverage, cycle-wave, two publish sinks); **Protocol-keyed three-part version scheme** (`ndmon`/`dmon` NuGet tools are ON the lockstep; only non-NuGet app artifacts version independently — reverses the current `Dmon.Network`-exempt scenario per ADR-035 D4); **Only the … projects are packable** (add the memory backends `Dmon.Memory`/`Dmon.Memory.Meko`). Add a requirement for the **app-artifact release family** (GitHub Release sink, runtime `agentReady` enforcement).

## Impact

- **Workflow:** `.github/workflows/release.yml` — rewritten around per-package tag prefixes + the two-family split. New packaging jobs for app artifacts (macOS build/sign deferred where noted in ADR-035 Open Questions).
- **Project files:** `memory/Dmon.Memory/Dmon.Memory.csproj` gains `IsPackable=true` + `PackageId`. Verify the ADR-024 D7 skew-guard (`Directory.Build.props` rejecting `Major.Minor` ≠ `ProtocolVersion.Current`) is present; add if missing.
- **Makefile:** may add a cycle-wave/release helper (e.g. `make release-wave`) and align `make network`'s hardcoded `--version 0.1.0` with MinVer.
- **ADRs:** implements ADR-035 (all decisions); honours ADR-023/024/025.
- **Out of scope (ADR-035 Open Questions):** `services/Dcal`/`Dmail` server deploy story; macOS `.app`/`.dmg` signing/notarization (packaging job may be scaffolded but signing deferred); ADR-024 OQ-D cadence policy.
