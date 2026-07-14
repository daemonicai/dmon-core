# Design — build-config hygiene (finding #9)

## Context — current build-config layout (verified)

MSBuild props files present in the repo (excluding `build/` output):

- `Directory.Build.props` (root) — Authors/Company/metadata, symbol packages, deterministic-build flags, `IsPackable=false` default, `IsProtocolKeyedPackage`, MinVer floor, the protocol-version skew-guard target, **and** `NoWarn;NU1903` (repo-wide). It does **not** set `TreatWarningsAsErrors` or `Nullable`.
- `Directory.Packages.props` (root) — CPM catalogue (single `<PackageVersion>` per package), `GlobalPackageReference` for MinVer + SourceLink, `CentralPackageFloatingVersionsEnabled=true`, and `Markdig` pinned to `1.*`.
- `default-core/Directory.Packages.props` — a CPM **opt-out** shadow (`ManagePackageVersionsCentrally=false`) for the file-based `default-core/Dmon.cs` dotnet-run program. It shadows the root *Packages* props only; there is **no** `default-core/Directory.Build.props`, so `default-core` still inherits the root `Directory.Build.props`.

There are **no other** nested `Directory.Build.props` or `Directory.Packages.props` files. The root `Directory.Build.props` is therefore the single centralization target for 9a.

Verified spread (46 `.csproj` total, `build/` excluded):

- `TreatWarningsAsErrors`: present in **45** of 46 — hand-copied. Missing from exactly **`samples/Dmon.ExtensionSmoke`** (#9b).
- `<Nullable>enable</Nullable>`: present in **all 46** — hand-copied.
- `Microsoft.Data.Sqlite` (the `NU1903` transitive's source): referenced by exactly **6** projects — `core/Dmon.Core`, `memory/Dmon.Memory`, `services/Dcal`, `services/Dmail`, `test/Dmon.Memory.Tests`, `test/Dcal.Tests`. (The audit's guess of "likely `Dmon.Memory`" undercounts — it is 6, spanning `core/`, `memory/`, `services/`, and `test/`.)
- `Markdig`: referenced by 1 project (`frontends/Dmon.Terminal`); `1.*` currently resolves to **`1.3.2`** (verified from the restored `project.assets.json` and the local NuGet cache).

## Goals

1. Centralize `TreatWarningsAsErrors=true` and `Nullable=enable` in the root `Directory.Build.props`; delete every per-`.csproj` duplicate. **Net behaviour unchanged** for the 45 already-TWE projects.
2. Bring `Dmon.ExtensionSmoke` under TWE (via 1), fixing — not suppressing — any latent warning it surfaces.
3. Scope `NoWarn;NU1903` to only the 6 `Microsoft.Data.Sqlite` consumers; drop it from the root.
4. Pin `Markdig` to `1.3.2`; remove the `CentralPackageFloatingVersionsEnabled` opt-in.

## Non-goals

- No change to the shared block already in the root props (MinVer, SourceLink, skew-guard, metadata, symbol packages).
- No change to the `default-core` CPM opt-out mechanism (`ManagePackageVersionsCentrally=false` stays).
- No bumping of any package version other than the `Markdig` `1.*`→`1.3.2` pin. `Microsoft.Data.Sqlite` is **not** upgraded here (the `NU1903` advisory stays suppressed where it applies, exactly as today — only its blast radius narrows).
- No new suppression of any warning surfaced by 9a/9b — those are fixed at source.

## Key decisions

### D1 — Centralization target (9a): root `Directory.Build.props`, single file

No nested `Directory.Build.props` exists, so the hoist is a single `<PropertyGroup>` addition to the root:

```xml
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<Nullable>enable</Nullable>
```

Then remove the two properties from all 46 `.csproj`. Because MSBuild applies the root props to every project (and to `default-core`, which has no `Directory.Build.props` shadow), the net effect on the 45 already-TWE + already-Nullable projects is **identical**. Individual `.csproj` may keep other settings (`ImplicitUsings`, `AssemblyName`, etc.) — only these two properties move.

### D2 — Samples are in-scope for TWE (9b)

**Decision: include samples.** `Dmon.ExtensionSmoke` becomes TWE via D1. Rationale: the whole repo is warning-clean by rule; a sample that isn't held to it is precisely the drift that let #9b happen. Because it currently compiles only *without* TWE, D1 may surface latent warnings **as errors** — those are **fixed at source**, never re-suppressed. (It is a minimal one-`PackageReference` consumer, so exposure is small; the `make build` gate will reveal anything.)

### D3 — `NU1903` scoping (9c): the 6 `Microsoft.Data.Sqlite` consumers

Remove `<NoWarn>$(NoWarn);NU1903</NoWarn>` from the root `Directory.Build.props`. Add `<NoWarn>$(NoWarn);NU1903</NoWarn>` to exactly the 6 projects that reference `Microsoft.Data.Sqlite`:
`core/Dmon.Core`, `memory/Dmon.Memory`, `services/Dcal`, `services/Dmail`, `test/Dmon.Memory.Tests`, `test/Dcal.Tests`.

Per-`.csproj` placement is chosen over a new per-area `Directory.Build.props` because the consumers span four buckets (`core/`, `memory/`, `services/`, `test/`) — no single area file covers them, and scattering four new props files is worse than 6 one-line `.csproj` entries. The explanatory comment (advisory id, why suppressed, the upgrade trigger) moves with the suppression. This keeps the advisory suppressed where it genuinely applies while restoring the `NU1903` signal for the other 40 projects.

### D4 — `Markdig` exact pin (9d): `1.3.2`, drop floating opt-in

Change `Directory.Packages.props`: `Markdig` `1.*` → `1.3.2` (the version `1.*` resolves to today, so restore output is unchanged). Then remove `<CentralPackageFloatingVersionsEnabled>true</CentralPackageFloatingVersionsEnabled>` and its comment — it exists solely to permit the Markdig float (per its own comment) and no other package floats. This makes restores reproducible and closes the door on any future accidental float.

## Risks

- **Latent warnings surfacing as errors (primary risk).** Any project that compiles today only because it lacks TWE — `Dmon.ExtensionSmoke` (D2), and potentially the `default-core/Dmon.cs` file-based program which newly inherits centralized TWE with no shadowing `Directory.Build.props` — may fail `make build` once TWE applies. **Mitigation:** the `make build` gate must be run and every surfaced warning **fixed at source** (not suppressed, not `#pragma`'d, not `NoWarn`'d). If a surfaced warning cannot be legitimately fixed within this change's build-config scope, that is a **stop-and-ask**, not a suppression.
- **`default-core` file-based program.** Confirm that centralizing TWE/Nullable does not break the `dotnet run`/`--no-build` core-launch path. It inherits the root `Directory.Build.props` (only the *Packages* props is shadowed). Verify `make build` covers it or exercise it explicitly.
- **Accidental behaviour flip.** The change must not turn TWE **off** anywhere it is on, nor turn it **on** where an unfixed warning lives without also fixing that warning. The `make build` gate (0 warnings, TWE clean) is the arbiter.

## Already-fixed / dropped sub-items

None. All of #9a–#9d are current as of `main` — verified against the live tree and the archived `ci-hardening` change (which did not touch `Directory.Build.props`, `Directory.Packages.props`, TWE/Nullable centralization, `NU1903` scoping, or the Markdig pin). Full #9a–#9d scope is retained.
