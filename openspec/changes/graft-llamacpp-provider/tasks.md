## 1. History-preserving import

- [x] 1.1 Acquire `git filter-repo` for the session (`uvx git-filter-repo --version`; fallback `brew install git-filter-repo`). Do not install into the repo.
- [x] 1.2 Clone the local-only source to a throwaway dir (`git clone /Users/rendle/github/daemonicai/dmon-llama-cpp /tmp/graft-llamacpp`); confirm it is on `main` @ `f9c6c4d` and clean. Never mutate the original repo. _(Needed `--no-local` so filter-repo accepts the clone as fresh.)_
- [x] 1.3 In the clone, run `git filter-repo` keeping only `src/Dmon.Extensions.LlamaCpp/` + `test/Dmon.Extensions.LlamaCpp.Tests/` with `--path-rename` to `providers/Dmon.Providers.LlamaCpp/` and `test/Dmon.Providers.LlamaCpp.Tests/` respectively (drops the satellite's `openspec/`, `nuget.config`, vendored nupkgs, root README/LICENSE, CI, `Directory.*`).
- [x] 1.4 On `change/graft-llamacpp-provider`, add the filtered clone as a remote, `git fetch`, `git merge --allow-unrelated-histories`, then remove the remote and delete the clone. Verify history with `git log --follow providers/Dmon.Providers.LlamaCpp/LlamaCppProviderExtension.cs` (shows pre-graft commits).

## 2. Rename to the provider family (Dmon.Providers.LlamaCpp)

- [x] 2.1 `git mv` the provider `.csproj` → `Dmon.Providers.LlamaCpp.csproj` and the test `.csproj` → `Dmon.Providers.LlamaCpp.Tests.csproj`.
- [x] 2.2 Set `AssemblyName`/`RootNamespace`/`PackageId` = `Dmon.Providers.LlamaCpp`; rewrite `namespace Dmon.Extensions.LlamaCpp` → `Dmon.Providers.LlamaCpp` across the src files and `Dmon.Extensions.LlamaCpp.Tests` → `Dmon.Providers.LlamaCpp.Tests` across the test files; update `UseLlamaCppExtensions.cs` `using` and the `InternalsVisibleTo` target.
- [x] 2.3 Repo-wide grep for `Dmon.Extensions.LlamaCpp` (excluding `bin/obj`) returns nothing.

## 3. Re-wire to monorepo conventions (ProjectReference, CPM, props)

- [x] 3.1 Provider `.csproj`: replace `PackageReference Include="Dmon.Abstractions"` with a `ProjectReference` to `core/Dmon.Abstractions`; remove the standalone `<Version>` (MinVer drives it) and any inline `PackageReference Version=`.
- [x] 3.2 Test `.csproj`: repoint its `ProjectReference` to the renamed provider under `providers/`; drop inline `Version=` from its `PackageReference`s.
- [x] 3.3 Add missing third-party pins to root `Directory.Packages.props` as `<PackageVersion>` — `Microsoft.Extensions.AI.OpenAI` aligned to the repo's existing `Microsoft.Extensions.AI` line (not the satellite's stray pin), plus any test deps not already centrally pinned from Phase 0; reuse existing entries, do not duplicate. Resolve any `NU1010`/`NU1605`.
- [x] 3.4 Ensure the provider is packable and consistent with sibling providers (e.g. Omlx): `IsPackable=true`, `MinVerTagPrefix`, `Description`; no per-project build settings that re-define the root `Directory.Build.props`.

## 4. Solutions

- [x] 4.1 Add `providers/Dmon.Providers.LlamaCpp/Dmon.Providers.LlamaCpp.csproj` to `providers.slnx` (under `/providers/`) and its test project under `/test/`.
- [x] 4.2 Add both projects to `Everything.slnx`; confirm no duplicate or dangling entries.

## 5. Verification gates

- [x] 5.1 `dotnet build Everything.slnx -c Release` clean (no warnings; `TreatWarningsAsErrors`).
- [x] 5.2 `make build` clean and `make test` green across `Everything.slnx`, including the grafted `Dmon.Providers.LlamaCpp.Tests`.
- [x] 5.3 `dotnet pack providers/Dmon.Providers.LlamaCpp/Dmon.Providers.LlamaCpp.csproj -c Release` succeeds with MinVer + the protocol skew-guard (`core/Dmon.Protocol/ProtocolVersion.cs`) intact.
- [x] 5.4 Assert no intra-repo first-party `PackageReference` remains in the grafted projects (all `ProjectReference`).
- [x] 5.5 `openspec validate graft-llamacpp-provider --strict` passes.
- [x] 5.6 Record `dmon-llama-cpp` as absorbed/read-only (DEVLOG + memory note; optional local `absorbed-into-dmon-core` tag on its `main`). Leave the original repo intact.
