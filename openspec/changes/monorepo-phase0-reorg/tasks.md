## 1. Cleanup ghost dirs and cruft

- [ ] 1.1 Remove the four untracked ghost dirs under `src/` (`Dmon.Extensions`, `Dmon.Providers`, `Dmon.BuiltinTools`, `Dmon.Tui`) — `bin`/`obj`-only, no tracked files
- [ ] 1.2 Remove other stale build cruft not covered by `.gitignore` (verify with `git status` that nothing tracked is deleted)
- [ ] 1.3 Confirm `make build` + `make test` still green at the current layout (baseline before any move)

## 2. Central Package Management (at current layout)

- [ ] 2.1 Add root `Directory.Packages.props` with `ManagePackageVersionsCentrally=true` and a `<PackageVersion>` per third-party dependency currently pinned inline
- [ ] 2.2 Remove inline `Version=` from every `PackageReference` across all `.csproj` (leave `PrivateAssets`/build-time refs ADR-conformant)
- [ ] 2.3 Resolve any `NU1010`/"version not centrally defined" errors; `make build` clean and `make test` green

## 3. Bucket moves, reference repair, solutions, and nested props

- [ ] 3.1 Create buckets and `git mv` core projects into `core/` (Abstractions, Protocol, Core, Runtime, Protocol.SchemaGen)
- [ ] 3.2 `git mv` providers into `providers/` (Anthropic, OpenAI, Gemini, Ollama)
- [ ] 3.3 `git mv` Tools.Builtin → `tools/`, Memory → `middleware/`, Terminal + Gateway → `frontends/`
- [ ] 3.4 Rewrite every `<ProjectReference>` relative path (and test-project references in `test/`) to the new bucket locations
- [ ] 3.5 Delete `Dmon.slnx`; create per-area solutions `core.slnx`, `providers.slnx`, `tools.slnx`, `middleware.slnx`, `frontends.slnx`
- [ ] 3.6 Create root `Everything.slnx` including every first-party project and every project under `test/`
- [ ] 3.7 Split `Directory.Build.props`: keep shared block at root; add per-area `Directory.Build.props` that chain-import the root for area deltas
- [ ] 3.8 Update the skew-guard `_ProtocolVersionFile` to `core/Dmon.Protocol/ProtocolVersion.cs`
- [ ] 3.9 `dotnet build Everything.slnx -c Release` clean; each area `.slnx` builds in isolation; `make test` green

## 4. Omlx provider relocate and rename

- [ ] 4.1 `git mv extensions/Dmon.Extensions.Omlx providers/Dmon.Providers.Omlx`; rename the `.csproj`; move its test project (path-repair its references)
- [ ] 4.2 Set `AssemblyName`/`RootNamespace`/`PackageId` to `Dmon.Providers.Omlx`, flip to packable, update `namespace`/`using` in its source and tests
- [ ] 4.3 Add Omlx to `providers.slnx` + `Everything.slnx`; confirm no lingering reference to the old assembly name; build + Omlx tests green

## 5. Tooling and CI

- [ ] 5.1 Update `Makefile` targets (build/test/clean/pack) to the new solutions/paths
- [ ] 5.2 Update `scripts/` (`pack-core.sh`, `smoke-cache.sh`, `smoke-sdk.sh`) and `default-core/Dmon.cs` build/run paths
- [ ] 5.3 Update the existing GitHub Actions workflow(s) to build/test/pack against the new layout (full dependency-aware path-filtering is a noted fast-follow, not this change)
- [ ] 5.4 Smoke-run `default-core/Dmon.cs` build+run; pack one packable project to confirm MinVer + skew-guard intact

## 6. Verification gates

- [ ] 6.1 `make build` clean (no warnings; `TreatWarningsAsErrors`) and `make test` green across `Everything.slnx`
- [ ] 6.2 Assert no intra-repo `PackageReference` to a first-party project remains (all `ProjectReference`)
- [ ] 6.3 `openspec validate monorepo-phase0-reorg --strict` passes
