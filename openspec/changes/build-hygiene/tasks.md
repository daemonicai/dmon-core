# Tasks — build-config hygiene (finding #9)

## 1. Centralize `TreatWarningsAsErrors` / `Nullable` (#9a, #9b)

- [ ] 1.1 Add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and `<Nullable>enable</Nullable>` to a `<PropertyGroup>` in the root `Directory.Build.props`.
- [ ] 1.2 Remove the duplicated `TreatWarningsAsErrors` property from all `.csproj` that declare it (45 projects).
- [ ] 1.3 Remove the duplicated `<Nullable>enable</Nullable>` property from all 46 `.csproj`.
- [ ] 1.4 Confirm `samples/Dmon.ExtensionSmoke` is now under TWE (via 1.1) and fix — at source, never suppress — any warning it (or the `default-core/Dmon.cs` file-based program) newly surfaces as an error. If a surfaced warning cannot be legitimately fixed within build-config scope, STOP AND ASK.

## 2. Scope the `NU1903` suppression (#9c)

- [ ] 2.1 Remove `<NoWarn>$(NoWarn);NU1903</NoWarn>` (and its explanatory comment) from the root `Directory.Build.props`.
- [ ] 2.2 Add `<NoWarn>$(NoWarn);NU1903</NoWarn>` with the explanatory comment to exactly the 6 `Microsoft.Data.Sqlite` consumers: `core/Dmon.Core`, `memory/Dmon.Memory`, `services/Dcal`, `services/Dmail`, `test/Dmon.Memory.Tests`, `test/Dcal.Tests`.

## 3. Pin `Markdig` and drop floating support (#9d)

- [ ] 3.1 Change `Markdig` in the root `Directory.Packages.props` from `1.*` to the exact version `1.3.2`.
- [ ] 3.2 Remove `<CentralPackageFloatingVersionsEnabled>true</CentralPackageFloatingVersionsEnabled>` and its comment from the root `Directory.Packages.props` (no package floats anymore).

## 4. Gates

- [ ] 4.1 `make build` is clean — 0 warnings, `TreatWarningsAsErrors` clean across the whole solution (including samples and `default-core`).
- [ ] 4.2 `env -u MEKO_API_KEY make test` — all tests green.
- [ ] 4.3 `openspec validate build-hygiene --strict` passes.
