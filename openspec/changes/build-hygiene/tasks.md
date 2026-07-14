# Tasks — build-config hygiene (finding #9)

## 1. Centralize `TreatWarningsAsErrors` / `Nullable` (#9a, #9b)

- [x] 1.1 Add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and `<Nullable>enable</Nullable>` to a `<PropertyGroup>` in the root `Directory.Build.props`.
- [x] 1.2 Remove the duplicated `TreatWarningsAsErrors` property from all `.csproj` that declare it (45 projects).
- [x] 1.3 Remove the duplicated `<Nullable>enable</Nullable>` property from all 46 `.csproj`.
- [x] 1.4 Confirm `samples/Dmon.ExtensionSmoke` is now under TWE (via 1.1) and fix — at source, never suppress — any warning it (or the `default-core/Dmon.cs` file-based program) newly surfaces as an error. If a surfaced warning cannot be legitimately fixed within build-config scope, STOP AND ASK.

## 2. Scope the `NU1903` suppression (#9c)

- [x] 2.1 Remove `<NoWarn>$(NoWarn);NU1903</NoWarn>` (and its explanatory comment) from the root `Directory.Build.props`.
- [x] 2.2 Add `<NoWarn>$(NoWarn);NU1903</NoWarn>` with the explanatory comment to exactly the projects that emit NU1903 once the repo-wide suppression is removed — the full **transitive** closure of `Microsoft.Data.Sqlite` consumers, not only the direct ones (NU1903 is a transitive restore-audit warning that flows across `ProjectReference` with no `PrivateAssets`). Empirically enumerated = **18 targets**: 13 `.csproj` (`core/Dmon.Core` [appended to its existing `NU1901`], `memory/Dmon.Memory`, `services/Dcal`, `services/Dmail`, `daemon/Daemon.Routing`, `frontends/Dmon.Network` [appended to `NU1901`], `test/Dmon.Core.Tests`, `test/Dmon.Memory.Tests`, `test/Dcal.Tests`, `test/Dmail.Tests`, `test/Daemon.Routing.Tests`, `test/Dmon.Network.Tests`, `test/Dmon.Tools.Dcal.Tests`) + 5 file-based composition roots via `#:property NoWarn=$(NoWarn);NU1903` (`default-core/Dmon.cs`, root `Dmon.cs`, `samples/Dmon.ComposedCore/Dmon.cs`, `samples/Dmon.MtplxCore/Dmon.cs`, `samples/Dmon.WebSearchCore/Dmon.cs`).

## 3. Pin `Markdig` and drop floating support (#9d)

- [x] 3.1 Change `Markdig` in the root `Directory.Packages.props` from `1.*` to the exact version `1.3.2`.
- [x] 3.2 Remove `<CentralPackageFloatingVersionsEnabled>true</CentralPackageFloatingVersionsEnabled>` and its comment from the root `Directory.Packages.props` (no package floats anymore).

## 4. Gates

- [x] 4.1 `make build` is clean — 0 warnings, `TreatWarningsAsErrors` clean (covers `Everything.slnx` core+terminal+memory and `default-core/Dmon.cs` via `build-core`). **`make smoke`** additionally builds `samples/Dmon.ExtensionSmoke` (out-of-tree, NOT compiled by `make build`/`make test`) 0-warn under the now-centralized TWE — the actual gate for the newly-TWE sample.
- [x] 4.2 `env -u MEKO_API_KEY make test` — all tests green.
- [x] 4.3 `openspec validate build-hygiene --strict` passes.
