## 1. Distribution ADR

- [x] 1.1 Write `docs/adrs/ADR-NNN-core-distribution.md` (next free number) recording: granular contract packages on nuget.org; `dmon` acquires `dmoncore` at runtime into the global NuGet cache rather than bundling or package-referencing it; the 3-part protocol-keyed version & compatibility scheme (`Major.Minor` = wire protocol, `Patch` = per-component counter); `dmoncore` ships as a runnable framework-dependent publish closure launched via `dotnet exec`
- [x] 1.2 Get the ADR accepted and add its row to the ADR table in `CLAUDE.md`; cross-reference it where distribution/versioning is discoverable

## 2. Protocol version single source of truth

- [x] 2.1 Add a public `ProtocolVersion` type to `Dmon.Protocol` with `const string Current = "0.1"` (and a parsed `Major.Minor` helper for comparison)
- [x] 2.2 Emit `ProtocolVersion.Current` from the core's `agentReady` (`RpcHostedService`), replacing the hardcoded `"1.0"`
- [x] 2.3 Add/adjust tests asserting the core emits `ProtocolVersion.Current` at `agentReady`

## 3. Dmon.Runtime library — discovery, acquisition, lifecycle, compatibility gate

- [x] 3.1 Create `src/Dmon.Runtime/` (`net10.0`, `IsPackable=false`); reference `Dmon.Protocol` and `NuGet.Protocol`; add to `Dmon.slnx` and the `make build`/test wiring
- [x] 3.2 Relocate `CoreProcessManager` from `Dmon.Terminal` to `Dmon.Runtime` (preserve start/stop/restart + stdio behaviour); update `Dmon.Terminal` to reference it
- [x] 3.3 Implement discovery precedence: `--core-path` → `DMON_CORE_PATH` → global NuGet cache (`SettingsUtility.GetGlobalPackagesFolder` + `GlobalPackagesFolderUtility.GetPackage`) → on-demand acquisition; overrides take priority over the cache
- [x] 3.4 Implement acquisition via `NuGet.Protocol`: `FindPackageByIdResource.GetAllVersionsAsync` filtered to the host's `Major.Minor`, newest wins; `CopyNupkgToStreamAsync`; `GlobalPackagesFolderUtility.AddPackageAsync` to install into the cache; actionable error on network failure naming the overrides
- [x] 3.5 Launch the resolved core from its cached publish closure via `dotnet exec` of `dmoncore.dll`
- [x] 3.6 Implement the protocol-version compatibility gate: read `agentReady.protocolVersion`, compare `Major.Minor` to `ProtocolVersion.Current`, stop the core and fail with an actionable mismatch error otherwise; expose the "start a protocol-compatible core" entry point (resolve Open Question: API seam) and route `Dmon.Terminal` (incl. `/reload`) through it
- [x] 3.7 Tests: discovery precedence (override > cache > fetch), cache-hit avoids network, version filter picks newest matching `Major.Minor`, compatibility gate accepts match / rejects mismatch, acquisition-failure error message

## 4. Packaging metadata and versioning

- [x] 4.1 Add `LICENSE` (MPL-2.0) at the repository root
- [x] 4.2 Add `Directory.Build.props` with shared metadata (Authors, RepositoryUrl, `PackageLicenseExpression=MPL-2.0`, SourceLink, `ContinuousIntegrationBuild`, deterministic builds, symbol packages) and `IsPackable=false` by default
- [x] 4.3 Set `IsPackable=true` only on `Dmon.Protocol`, `Dmon.Abstractions`, `Dmon.Extensions`, `Dmon.Terminal`, `Dmon.Core`; add per-package README and description
- [x] 4.4 Configure MinVer with per-project tag prefixes (`dmon-`, `core-`, and the SDK line — resolve Open Question: shared SDK version line); ensure inter-package deps (`Dmon.Extensions`/`Dmon.Abstractions` → `Dmon.Protocol`) pack as package dependencies
- [x] 4.5 Add a version-consistency guard (MSBuild target or release step) that fails when a packed version's `Major.Minor` diverges from `ProtocolVersion.Current`

## 5. Package shaping — tool and runnable core

- [x] 5.1 Make `Dmon.Terminal` a dotnet tool (`PackAsTool`, `ToolCommandName=dmon`); verify the tool package carries `Dmon.Runtime` + `NuGet.Protocol` transitively and declares **no** `dmoncore` dependency or payload
- [x] 5.2 Shape the `dmoncore` package to contain the full framework-dependent publish closure (deps.json + runtimeconfig + dependency assemblies) laid out for direct `dotnet exec`
- [x] 5.3 Verify the three SDK packages pack independently with correct dependency edges and an out-of-tree consumer compiles against them (a minimal sample/smoke project implementing `IDmonExtension`)
- [x] 5.4 Smoke-test `dotnet tool install` of the locally-packed `dmon` against a local feed: first run acquires `dmoncore` into the cache and reaches `agentReady`

## 6. Release pipeline

- [x] 6.1 Add a tag-triggered `release.yml` that runs `dotnet pack` + `dotnet nuget push` to nuget.org using a `NUGET_API_KEY` secret; ensure PR CI does not publish
- [x] 6.2 Document the release process and the `Dmon.*` nuget.org prefix reservation (HITL: reserving the prefix and adding the secret are manual nuget.org/GitHub steps — provide a copy-pasteable recipe) <!-- recipe documented in docs/releasing.md; performing the manual nuget.org prefix reservation + NUGET_API_KEY secret is deferred per the agreed build+local-smoke-only publishing scope -->

## 7. Verification

- [x] 7.1 `make build` clean (`TreatWarningsAsErrors`) and `make test` green
- [x] 7.2 `openspec validate core-distribution --strict`
- [x] 7.3 Confirm every scenario in the `package-publishing` and `core-runtime-acquisition` specs is covered by a test, a pack/inspection check, or the accepted ADR
