## 1. History-preserving import

- [x] 1.1 Ensure `git-filter-repo` is available (`uvx git-filter-repo --version`; fallback `brew install git-filter-repo`).
- [x] 1.2 `git clone --no-local -b feat/dmon-tool-dmail /Users/rendle/github/daemonicai/dmail /tmp/graft-dmail` (extension @ `b1af562`); never operate on the original repo. (`--no-local` + direct `-b` branch clone are required so filter-repo sees a fresh, single-reflog clone.)
- [x] 1.3 Run `uvx git-filter-repo` keeping only `src/Dmon.Extensions.Dmail/` and `test/Dmail.Tests/DmailExtensionTests.cs`, with `--path-rename`s to `tools/Dmon.Tools.Dmail/` and `test/Dmon.Tools.Dmail.Tests/DmailExtensionTests.cs` (per design).
- [x] 1.4 On branch `change/graft-dmail`, add the throwaway clone as a remote, `git merge --allow-unrelated-histories dmail-graft/feat/dmon-tool-dmail`, then remove the remote and delete `/tmp/graft-dmail`.
- [x] 1.5 Confirm only the intended paths landed (extension subtree incl. its `README.md` + the one test file); no server, server tests, satellite `openspec/`, `nuget.config`, vendored nupkgs, or `Directory.*` files imported.

## 2. Rename to the tool family (Dmon.Tools.Dmail)

- [x] 2.1 `git mv` the extension `.csproj` to `tools/Dmon.Tools.Dmail/Dmon.Tools.Dmail.csproj`; set `AssemblyName`/`RootNamespace`/`PackageId` = `Dmon.Tools.Dmail`.
- [x] 2.2 Rewrite the C# namespace `Daemonic.Dmail.Extension` → `Dmon.Tools.Dmail` across the src files (`DmailExtension`, `DmailClient`, `DmailModels`, `DmailApiException`).
- [x] 2.3 Rewrite the test namespace `Daemonic.Dmail.Tests` → `Dmon.Tools.Dmail.Tests` in `DmailExtensionTests.cs` and update its `using` of the extension namespace.
- [x] 2.4 Repo-wide grep (excluding `bin/obj`) for `Dmon.Extensions.Dmail` and `Daemonic.Dmail` returns nothing. (Includes the package `README.md` title + `#:package` pin + `using` — the `AddExtension` verb is the Group 3 concern and remains.)

## 3. API port to IToolExtension / Dmon.Abstractions (ADR-022)

- [x] 3.1 In `DmailExtension.cs`: `using Dmon.Extensions;` → `using Dmon.Abstractions.Extensions;`, and `: IDmonExtension` → `: IToolExtension` (method bodies unchanged; `DmonAIFunctionFactory` confirmed in `Dmon.Abstractions.Extensions` — same `using` covers both).
- [x] 3.2 Update the class doc-comment and the package `README.md` registration guidance from `AddExtension` to `builder.AddToolExtension<DmailExtension>()` (verb confirmed against core `DmonRegistrationExtensions`).
- [x] 3.3 Repo-wide grep (excluding `bin/obj`) for `IDmonExtension` and `using Dmon.Extensions;` in the grafted files returns nothing.

## 4. Re-wire to monorepo conventions (ProjectReference, CPM, fresh test csproj)

- [ ] 4.1 Tool `.csproj`: replace `<PackageReference Include="Dmon.Extensions" Version="0.2.*" />` with a `ProjectReference` to `..\..\core\Dmon.Abstractions\Dmon.Abstractions.csproj`.
- [ ] 4.2 Tool `.csproj`: remove the standalone `<Version>0.2.0</Version>` and strip inline `Version=` from `PackageReference`s; keep `IsPackable=true`, `MinVerTagPrefix`, packed `README.md`, and a `Description`/`PackageTags` consistent with `Dmon.Tools.Builtin`.
- [ ] 4.3 Author a fresh `test/Dmon.Tools.Dmail.Tests/Dmon.Tools.Dmail.Tests.csproj` (`IsPackable=false`, `RootNamespace=Dmon.Tools.Dmail.Tests`) referencing only `..\..\tools\Dmon.Tools.Dmail\Dmon.Tools.Dmail.csproj`; reuse central test pins (no inline `Version=`).
- [ ] 4.4 Add any missing third-party `<PackageVersion>` to root `Directory.Packages.props` only if a build surfaces one (expected: none).

## 5. Solutions

- [ ] 5.1 Add `tools/Dmon.Tools.Dmail/Dmon.Tools.Dmail.csproj` to `tools.slnx` (under `/tools/`) and the test to `tools.slnx` (under `/test/`).
- [ ] 5.2 Add both projects to `Everything.slnx`.

## 6. Verification gates

- [ ] 6.1 `dotnet build Everything.slnx -c Release` clean (no warnings; `TreatWarningsAsErrors`).
- [ ] 6.2 `make build` and `make test` green — the four ported `DmailExtensionTests` plus all existing tests.
- [ ] 6.3 `dotnet pack tools/Dmon.Tools.Dmail/Dmon.Tools.Dmail.csproj -c Release` succeeds — skew-guard passes and a sane MinVer `Major.Minor` matching `core/Dmon.Protocol/ProtocolVersion.cs` is produced.
- [ ] 6.4 `git log --follow tools/Dmon.Tools.Dmail/DmailExtension.cs` shows pre-graft commits (history preserved).
- [ ] 6.5 `openspec validate graft-dmail --strict` passes.
