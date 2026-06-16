## 1. Cleanup ghost dirs and cruft

- [x] 1.1 Remove the four untracked ghost dirs under `src/` (`Dmon.Extensions`, `Dmon.Providers`, `Dmon.BuiltinTools`, `Dmon.Tui`) — `bin`/`obj`-only, no tracked files
- [x] 1.2 Remove other stale build cruft not covered by `.gitignore` (verify with `git status` that nothing tracked is deleted)
- [x] 1.3 Confirm `make build` + `make test` still green at the current layout (baseline before any move)

## 2. Central Package Management (at current layout)

- [x] 2.1 Add root `Directory.Packages.props` with `ManagePackageVersionsCentrally=true` and a `<PackageVersion>` per third-party dependency currently pinned inline
- [x] 2.2 Remove inline `Version=` from every `PackageReference` across all `.csproj` (leave `PrivateAssets`/build-time refs ADR-conformant)
- [x] 2.3 Resolve any `NU1010`/"version not centrally defined" errors; `make build` clean and `make test` green

## 3. Bucket moves, reference repair, solutions, and nested props

- [x] 3.1 Create buckets and `git mv` core projects into `core/` (Abstractions, Protocol, Core, Runtime, Protocol.SchemaGen)
- [x] 3.2 `git mv` providers into `providers/` (Anthropic, OpenAI, Gemini, Ollama)
- [x] 3.3 `git mv` Tools.Builtin → `tools/`, Memory → `middleware/`, Terminal + Gateway → `frontends/`
- [x] 3.4 Rewrite every `<ProjectReference>` relative path (and test-project references in `test/`, plus extensions/Omlx and the path-coded `ToolPackTests.cs`) to the new bucket locations
- [x] 3.5 Delete `Dmon.slnx`; create per-area solutions `core.slnx`, `providers.slnx`, `tools.slnx`, `middleware.slnx`, `frontends.slnx`
- [x] 3.6 Create root `Everything.slnx` including every first-party project and every project under `test/`
- [x] 3.7 Keep the root `Directory.Build.props` as the shared base (applies repo-wide). Do NOT add empty per-area `Directory.Build.props` in Phase 0 — they are introduced only when an area needs a delta, and when added must chain-import the root via `GetPathOfFileAbove` rather than redefine it
- [x] 3.8 Update the skew-guard `_ProtocolVersionFile` to `core/Dmon.Protocol/ProtocolVersion.cs`
- [x] 3.9 `dotnet build Everything.slnx -c Release` clean; each area `.slnx` builds in isolation; full `dotnet test` green (gate via direct dotnet — `make` is restored in Group 5)

## 4. Omlx provider relocate and rename

- [x] 4.1 `git mv extensions/Dmon.Extensions.Omlx providers/Dmon.Providers.Omlx`; rename the `.csproj`; move its test project (path-repair its references)
- [x] 4.2 Set `AssemblyName`/`RootNamespace`/`PackageId` to `Dmon.Providers.Omlx`, flip to packable, update `namespace`/`using` in its source and tests
- [x] 4.3 Add Omlx to `providers.slnx` + `Everything.slnx`; confirm no lingering reference to the old assembly name; build + Omlx tests green

## 5. Tooling and CI

- [ ] 5.1 Update `Makefile` targets (build/test/clean/pack) to the new solutions/paths
- [ ] 5.2 Update `scripts/` (`pack-core.sh`, `smoke-cache.sh`, `smoke-sdk.sh`) and `default-core/Dmon.cs` build/run paths
- [ ] 5.3 Update the existing GitHub Actions workflow(s) to build/test/pack against the new layout (full dependency-aware path-filtering is a noted fast-follow, not this change)
- [ ] 5.4 Smoke-run `default-core/Dmon.cs` build+run; pack one packable project to confirm MinVer + skew-guard intact

## 6. Verification gates

- [ ] 6.1 `make build` clean (no warnings; `TreatWarningsAsErrors`) and `make test` green across `Everything.slnx`
- [ ] 6.2 Assert no intra-repo `PackageReference` to a first-party project remains (all `ProjectReference`)
- [ ] 6.3 `openspec validate monorepo-phase0-reorg --strict` passes
