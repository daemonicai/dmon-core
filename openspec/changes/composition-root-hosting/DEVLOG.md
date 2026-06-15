# DEVLOG: composition-root-hosting

<!-- Realizes ADR-019: dmoncore becomes a library with a DmonHost hosting surface; Dmon.cs is the composition root; the dynamic-load tier (.csx, config extension list, runtime NuGet downloader, extension.load RPC) is deleted. -->

Baseline before work: `make build` clean (0 warnings / 0 errors); `ProtocolVersion.Current = "0.2"`.

## 1. Hosting surface (`DmonHost`)

- Added `Dmon.Hosting` to the `dmoncore` library: `DmonHost.CreateBuilder(args)` → `DmonHostBuilder` (`WithModel`, `AddExtension`/`AddExtension<T>`, `WithPermissionMode`, `WithProfile`, `WithStdio`, `WithoutTelemetry`) → `Build()` → `DmonBuiltHost.RunAsync(ct)`. `Build()` holds the relocated `Program.cs` bootstrap (YAML config, logging, OTel, `AddDmon*`, `AddHostedService<RpcHostedService>`); `RunAsync` is the unchanged JSONL/stdio loop.
- **Stdio seam:** `TextReader`/`TextWriter` registered via `TryAddSingleton` (default `Console.In`/`Console.Out`); `RpcHostedService` reads `TextReader` from DI; `EventEmitter` flushes per line so framing is unchanged. `WithStdio` overrides win because the builder pre-registers before `AddDmonCore`'s `TryAdd`. Default spawned-process path is byte-identical.
- **Decision:** `WithPermissionMode` and `WithProfile` are implemented as **decorators** over `IAgentProfileResolver` (`PermissionModeOverrideResolver`, `ProfileOverrideResolver`), not config writes — config-write attempts were silent no-ops because the profile resolver reads its own file-based `ConfigurationBuilder`, not host `IConfiguration`. `WithModel` writes the scalar `["activeModel"] = "{provider}/{model}"` (matches `ModelRef.Parse`) layered after YAML so code wins over `config.yaml`. (Caught by reviewer as B1/B2.)
- **Decision:** dropped a `HostOptions.ShutdownTimeout=5s` the worker had added — not in the original `Program.cs`; D1 requires byte-for-byte relocation, so framework default (30s) restored.
- `Program.cs` reduced to a one-line shim (`await DmonHost.CreateBuilder(args).Build().RunAsync();`) — **kept runnable** so existing process-spawning tests pass.
- **1.3 deferred (not ticked):** "entry-point-less library / no `Main` in the package" needs the replacement entry point (canonical `Dmon.cs`, G2) and the library-packaging flip (G7) to exist first. Completes in G2/G7.
- Tests: `DmonHostGoldenPathTests` — `agentReady`/`protocolVersion=0.2` over a `Pipe`-backed in-proc stdio; a builder-registered `AIFunction` lands in `IToolRegistry`; `WithModel`/`WithProfile` overrides reach `ActiveModelStore`/the resolver (regression guards for B1/B2).
- Gates: `make build` 0/0; full `make test` green (628 Core tests, only pre-existing skips); `openspec validate --strict` valid.

## 2. `Dmon.cs` composition root, canonical default, and `dmon init`

- **2.1** `default-core/Dmon.cs` — canonical composition root: `#:package dmoncore@0.2.*` + `await DmonHost.CreateBuilder(args).Build().RunAsync();`, no extra extensions. Checked-in `default-core/nuget.config` → `../.pack-out` + nuget.org (built in-repo against the local feed; G7.2 publishes the prebuilt default from it).
- **2.2** `dmon init` — `src/Dmon.Terminal/InitCommand.cs` + an early `args.Length>0 && args[0]=="init"` dispatch in `Program.cs` (no-arg still launches the TUI). Scaffolds `Dmon.cs` into CWD with the pin derived from `ProtocolVersion.Current` (no hardcoded duplicate), no local-feed nuget.config (real users resolve from nuget.org), refuses to overwrite an existing file (exit 1).
- **2.3** Sample composed root: `samples/Dmon.SampleExtension/` (packable `IDmonExtension` with a `greet` tool) packed into the feed by `pack-core.sh`; `samples/Dmon.ComposedCore/Dmon.cs` declares `#:package dmoncore@0.2.*` + `#:package Dmon.SampleExtension@0.2.*` + `.AddExtension<GreetingExtension>()` — proves compile-time composition via `#:package` (not `#:project`).
- **2.4 / Decision (test infra):** the build/compose integration tests **self-provision** — `InitFeedFixture`/`ComposedCoreFeedFixture` (`IAsyncLifetime`) pack into a **unique per-fixture temp feed** (Guid-named, torn down), so tests always really run with no shared-`.pack-out` race and **no silent vacuous-pass** (reviewer's B1). Verified green with `.pack-out` deleted (Terminal 12s / Core 13s — real builds+spawns). `pack-core.sh` gained an optional `$1` target-feed arg (default `.pack-out`).
- **N1 fix:** wire test proves the no-runtime-load half (real composed core, `agentReady` with no `extensionLoaded`); a separate in-process test positively asserts `AddExtension<T>()` lands `greet` in `IToolRegistry` (no wire tool-enumeration command exists today — documented). Test (c) asserts single contract-type identity (`IDmonExtension` Type match) — fails under a separate/collectible ALC.
- **N2 fix:** guard tests assert `default-core/Dmon.cs` and `samples/Dmon.ComposedCore/Dmon.cs` contain `dmoncore@{ProtocolVersion.Current}.*` (derived, repo-root located from assembly path; missing file fails).
- **Nit deferred (N3):** `Dmon.SampleExtension.csproj` hardcodes `Dmon.Extensions Version="0.2.*"` — consistent with the floating-pin scheme; not guarded.
- **Tooling (post-G2, commit `5663305`, user-requested):** added `make pack` (wraps `scripts/pack-core.sh`, accepts `PACK_OUT`) and `make smoke` (wraps `scripts/smoke-sdk.sh`) for discoverability/CI. **`make test` stays independent of `pack`** — the integration tests self-provision per-fixture temp feeds, so a shared `.pack-out` is a manual/CI convenience, not a test prerequisite. The script remains the reusable unit (fixtures call `pack-core.sh "<temp-feed>"`); `make` and the fixtures are both callers.

## 7. Packaging

*(Task 7.1 pulled ahead of Group 2 — G2's "compose a Dmon.cs against `#:package dmoncore`" hard-depends on dmoncore being a library package. 7.2/7.3 remain in the G7 slot.)*

- **7.1 done** (committed standalone): `dmoncore` (`src/Dmon.Core`) now packs as a **library** package. `Dmon.Core.csproj`: set `<PackageId>dmoncore</PackageId>`; removed the root-closure machinery (`_AddPublishClosureToPackage` target, `TargetsForTfmSpecificContentInPackage`/`_PublishClosureStageDir`, `IncludeBuildOutput=false`, `IncludeSymbols=false`, `SuppressDependenciesWhenPacking`, `NU5100;NU5128` NoWarn) so it produces `lib/net10.0/dmoncore.dll`.
- **Decision (user):** the three **non-packable** impl refs (`Dmon.BuiltinTools`, `Dmon.Providers`, `Dmon.Providers.Ollama`) are **bundled** into `lib/net10.0/` via a `CopyImplDepsToPackage` target (`TargetsForTfmSpecificBuildOutput`, filtered by exact filename, `PrivateAssets="all"` on those refs). The packable contract trio (`Protocol`/`Abstractions`/`Extensions`) stay as package **dependencies**. The bundled DLLs' external NuGet deps were declared on dmoncore: added `Anthropic` 12.24.1, `GeminiDotnet.Extensions.AI` 0.25.0, `Microsoft.Extensions.AI.OpenAI` 10.5.1, `OllamaSharp` 5.4.25.
- **Decision (user):** dev acquisition = local `.pack-out` feed (existing `smoke-sdk.sh` precedent). New `scripts/pack-core.sh` packs the contract trio + dmoncore at a **stable** `0.2.0` (`-p:MinVerVersionOverride=0.2.0`) so a spec-literal `#:package dmoncore@0.2.*` resolves (untagged MinVer would yield a prerelease `0.2.*` won't match). Skew guard (`CheckProtocolVersionSkew`) passes legitimately — the override sets MinVerMajor/Minor=0/2 == `ProtocolVersion.Current`.
- Verified empirically (worker + reviewer re-packed): nupkg `lib/` has exactly the 4 expected DLLs; nuspec deps complete; a throwaway `Dmon.cs` with `#:package dmoncore@0.2.*` compiles against the feed. `dotnet publish` (→ `make build`) is unaffected; full suite green (1011 tests).
- **7.2 done:** the prebuilt default-core closure (publish of `default-core/Dmon.cs` → `build/dmoncore/`, runnable via `dotnet exec` with no SDK/restore) is bundled into the `dmon` tool package as a **file payload**, not a NuGet dependency. `Dmon.Terminal.csproj` gains `CollectPrebuiltDefaultCore` + `CopyPrebuiltDefaultCoreToPublish` (AfterTargets=Publish → `<publishDir>/dmoncore/`) + `StagePrebuiltDefaultCoreForPack` (BeforeTargets=GenerateNuspec → `tools/net10.0/any/dmoncore/`). Per-item inline `Pack`/`PackagePath` metadata avoids MSB4096 batching over the shared `None` list (README has no PackagePath). `CoreResolver` Tier-3 dev-layout candidate moved to `build/dmoncore/` to match the publish output.
- **7.3 done:** `Packaging/PackagingChecksTests` (dmoncore nupkg = referenceable library `lib/net10.0/Dmon.Core.dll`, no closure runtimeconfig outside `lib/`; prebuilt closure has `dmoncore.dll` + deps + `runtimeconfig.json`) and `Packaging/ToolPackTests` (`dotnet pack` Terminal → payload at `tools/net10.0/any/dmoncore/`, nuspec has **no** `<dependency>` on `dmoncore`/`Dmon.DefaultCore`). New `ComposedCoreBuildCollection` serialises all SDK-shelling test classes (`DisableParallelization=true`) to avoid concurrent nested-MSBuild MSB4166 crashes.
- **Flake fixed (committed standalone `87ea839`):** core-launching test fixtures shared `build/dmoncore/appsettings.json` as the host content root and rewrote it non-atomically in parallel; `Host.CreateApplicationBuilder` throws on an empty/malformed file, so a booting core occasionally read it mid-truncate (0 bytes) and crashed `ConsoleSmokeTest`. Each `CoreProcessFixture` now uses its own temp content-root dir; `BadConfigEntryIntegrationTest` writes to its own working dir.
- **Reviewer arch-note 1 fixed:** packaging tests hard-failed when `build/dmoncore/` was absent. (a) Makefile `test: build-core` so the canonical entry point always has the closure; (b) tests are `[SkippableFact]` (xUnit 2.9.3 → `xunit.skippablefact` 1.4.13; `Assert.Skip` is v3-only) and `Skip.If` the prereq-missing case so a bare `dotnet test` skips (not fails). A genuine `dotnet pack` failure still **fails** (separate `_packError` path), not skips.

## 3. `Dmon.Runtime` launch: precedence + build-then-`--no-build`-run

*(Task 5.1 downloader deletion pulled ahead — the NuGet-cache/on-demand tiers were deleted here as they directly depend on the same code being reworked.)*

- **3.1** `CoreResolver` rewritten to 3-tier precedence (no `ICoreAcquisitionSource` dependency, no `async`/`await` — resolution is pure filesystem). Tier 1: `Dmon.cs` in working dir → `LaunchMode.FileBasedProgram`; Tier 2: `--core-path` / `DMON_CORE_PATH` → `LaunchMode.DirectExecutable`; Tier 3: published-sibling or dev-bin → `LaunchMode.DirectExecutable` or `LaunchMode.DotnetExec` (dll closure). Throws `CoreAcquisitionException` mentioning `Dmon.cs`, `--core-path`, and `DMON_CORE_PATH` if all tiers miss.
- **3.2** `ResolvedCore` — added `LaunchMode.FileBasedProgram` (path = absolute path to `Dmon.cs`). `CoreProcessManager.BuildFileBasedProgramAsync(dmonCsPath, ct)`: runs `dotnet build <Dmon.cs>` as a separate fully-captured process (stdout+stderr redirected; incremental SDK up-to-date check is the staleness gate). `CoreProcessManager.BuildProcessStartInfo`: added `FileBasedProgram` branch → `dotnet run <Dmon.cs> --no-build`. `CoreLauncher.StartProtocolCompatibleCoreAsync`: builds first (if `FileBasedProgram`), then starts the stdio child.
- **3.3** `CoreLauncher.RestartAsync`: inspects `current.Process` with `is CoreProcessManager mgr` cast; if `mgr.ResolvedCore.LaunchMode == FileBasedProgram`, calls `BuildFileBasedProgramAsync` (incremental) before `RestartAsync`. An unchanged `Dmon.cs` is a near-no-op — the SDK up-to-date check returns immediately.
- **5.1 (pulled ahead):** `NuGetCoreAcquisitionSource.cs` and `ICoreAcquisitionSource.cs` deleted. `FakeCoreAcquisitionSource.cs` (test) deleted. `NuGet.Protocol` removed from `Dmon.Runtime.csproj`.
- **OQ-B resolution: `dotnet run --no-build` branch taken.** Empirical test against `default-core/Dmon.cs` (local feed): `dotnet build default-core/Dmon.cs` → exit 0; built DLL at `~/Library/Application Support/dotnet/runfile/Dmon-<hash>/bin/debug/Dmon.dll`. Then `dotnet run default-core/Dmon.cs --no-build` (stdin open, stdout captured, stderr discarded): **first stdout line = `{"type":"agentReady","protocolVersion":"0.2","coreVersion":"0.0.0.0"}`** — pure JSONL, no MSBuild/banner output. The exec-the-built-dll fallback is not needed.
- **Tests:** `CoreResolverTests` (8 tests) fully rewritten to the 3-tier model — no `FakeCoreAcquisitionSource`, no NuGet types. `CompatibilityGateTests` (5 tests) unchanged and passing. Two new integration tests in `Dmon.Core.Tests/Composition/FileBasedProgramLaunchTests.cs` (reuse `ComposedCoreFeedFixture`): `FileBasedProgram_FirstBuild_FirstStdoutLineIsAgentReady` and `FileBasedProgram_Rebuild_AfterEdit_FirstStdoutLineIsAgentReady` — both spawn a real `Dmon.cs` core and assert the first stdout line is a valid JSONL `agentReady` frame.
- Gates: `make build` 0/0; `make test` 1,302 passed, 2 pre-existing skips, 0 failures.

### Group 3 — reviewer fixes (B1, N1, N3)

- **B1 (blocker) resolved — Route 1: default and override now `dotnet exec dmoncore.dll`.**
  - `CoreResolver` tier 2 (override): `.dll` path → `DotnetExec`; non-`.dll` path kept as `DirectExecutable` (dev/escape-hatch). Private helper `OverrideLaunchMode(path)` encodes the rule.
  - `CoreResolver` tier 3 (default): dropped the bare `dmoncore` apphost check; looks only for `dmoncore.dll` (published sibling, then dev-bin `Debug/Release`) → `DotnetExec`. Dev-bin candidates updated from `dmoncore` to `dmoncore.dll`. Build confirms `src/Dmon.Core/bin/Release/net10.0/dmoncore.dll` exists after `dotnet build`.
  - `DirectExecutable` enum value retained — it is produced by the non-`.dll` override escape hatch and consumed by `BuildProcessStartInfo`'s `default` branch. Not dead code.
- **N1 resolved — public `CoreProcessManager(string? corePathOverride, …)` ctor and private `ResolveDirectExecutable` deleted.** No callers existed outside the two Terminal tests (confirmed by grep). Those tests updated to call the internal `CoreProcessManager(ResolvedCore, …)` ctor directly (accessible via the existing `InternalsVisibleTo("Dmon.Terminal.Tests")`) with a `ResolvedCore(dll, DotnetExec)` produced by their `FindCoreDll()` helper. One precedence implementation remains: `CoreResolver`.
- **N3 resolved — `--tl:off` added to `BuildFileBasedProgramAsync` dotnet build arg list.** Keeps captured build output readable.
- **Test updates:** `CoreResolverTests` expanded from 8 to 15 tests. Old `OverridePath_WinsOverDefault` (single test, asserted `DirectExecutable`) split into four: `.dll` override → `DotnetExec`; non-`.dll` override → `DirectExecutable`; `DMON_CORE_PATH` `.dll` → `DotnetExec`; `DMON_CORE_PATH` non-`.dll` → `DirectExecutable`. A comment-only section notes that tier-3 `DotnetExec` assertion for published sibling is covered by the override-dll tests (same `OverrideLaunchMode` code path) and the integration build.
- Gates: `make build` 0 warnings / 0 errors; `make test` all pass (15 Runtime, 160 Terminal — including the `DotnetExec`-launched integration tests; 0 failures).

## 4. Delete the dynamic-load tier

**Deleted production files (~1,100 LOC removed):**

- `src/Dmon.Core/Extensions/CsxScriptLoader.cs` (~236 LOC) — Roslyn `.csx` hot-loader
- `src/Dmon.Core/Extensions/NuGetExtensionLoader.cs` (~407 LOC) — `AssemblyLoadContext.Default` static ctor, `AssemblyDependencyResolver` probing, middleware reflection-discovery pass
- `src/Dmon.Core/Extensions/ExtensionService.cs` — orchestrated load/unload/promote and middleware registry hand-off
- `src/Dmon.Core/Extensions/IExtensionLoader.cs` — loader interface and `ExtensionLoadConfirmRequest`
- `src/Dmon.Core/Extensions/ExtensionLoadResult.cs` — `ParsedExtensionSource`
- `src/Dmon.Core/Extensions/StartupExtensionLoader.cs` — config-declared startup loader
- `src/Dmon.Core/Extensions/PromoteService.cs` — `.csx`→NuGet scaffolder
- `src/Dmon.Core/BuiltinTools/ExtensionLoadTool.cs` — the `extension.analyze` agent tool
- `src/Dmon.Core/Rpc/ConfigExtensionHandler.cs` / `IExtensionHandler.cs` — RPC extension-load handler
- `src/Dmon.Core/Config/ExtensionEntry.cs`, `ExtensionsConfigReader.cs`, `EffectiveExtensionSetResolver.cs`, `ExtensionSourceNormalizer.cs`
- `src/Dmon.Protocol/Commands/ExtensionCommands.cs` — `extension.load` / `extension.unload` / `extension.promote` commands

**Deleted test files (~800 LOC):**

- `test/Dmon.Core.Tests/Extensions/` — `CsxScriptLoaderTests`, `ExtensionServiceTests`, `MiddlewareConfigServiceProviderTests`, `NuGetExtensionLoader*Tests` (×4), `PromoteServiceTests`, `StartupExtensionLoaderTests`, `ParsedExtensionSourceTests`, `TestAssemblyEmitter`
- `test/Dmon.Core.Tests/Config/EffectiveExtensionSetResolverTests.cs`
- `test/Dmon.Core.Tests/Rpc/ConfigExtensionHandlerTests.cs`
- `test/Dmon.Terminal.Tests/ConfigReflectedAfterReloadTests.cs`

**Modified — protocol surface (`src/Dmon.Protocol`):**

- `Command.cs` — removed three `[JsonDerivedType]` attrs: `ExtensionLoadCommand`, `ExtensionUnloadCommand`, `ExtensionPromoteCommand`
- `Event.cs` — removed `[JsonDerivedType]` for `ExtensionLoadedEvent`, `ExtensionUnloadedEvent`
- `Events/OtherEvents.cs` — deleted both event record definitions
- `docs/protocol/schema.json` regenerated via `make schema` (schema golden-file test now passes)

**Modified — middleware registry → builder surface:**

- `IMiddlewareRegistry` / `MiddlewareRegistry` — `Register(IReadOnlyList<IDmonMiddleware>, int? priorityOverride)` + `GetAll()` returning `(Middleware, PriorityOverride)` tuples
- `MiddlewarePipelineBuilder.Apply` — sources middleware from `GetAll()` tuples; `EffectivePriority` uses registration override > config > attribute > 0
- `DmonHostBuilder` — new `AddMiddleware(IDmonMiddleware, int?)` (instance) and `AddMiddleware<T>(int?)` (type-based, `ActivatorUtilities.CreateInstance`) overloads; registered middleware folded into `IMiddlewareRegistry` in `Build()` after the DI container is built
- `DaemonServiceExtensions.AddDmonExtensions()` — stripped config/runtime loader registrations; kept `IToolRegistry`, `IMiddlewareRegistry`, `MiddlewarePipelineBuilder`, `ProfilesConfigReader`, `EffectiveProfileSetResolver`, security helpers
- `CommandDispatcher` — removed `IExtensionHandler` field and three command routes
- `RpcHostedService` — removed `StartupExtensionLoader` field, ctor param, and call
- `SlashCommandParser` — removed `load`/`unload`/`promote` slash-command branches
- `ConsoleEventHandler` — removed `ExtensionLoadedEvent` / `ExtensionUnloadedEvent` silent-event cases
- `BuiltinToolsRegistration` — removed `ExtensionLoadTool`; kept `ExtensionSearchTool`, `ExtensionReadmeTool`

**Task 4.5 — confirmed retained** (no deletions needed):

- `IDmonExtension` / `AIFunction`, `IDmonMiddleware` / `DmonMiddlewareAttribute`, permission pipeline all intact.

**OQ-C resolution:** The `docs/protocol/schema.json` golden file referenced the extension commands/events (as the `CommittedSchema_MatchesLiveExport` test proved), but the `openspec/specs/protocol-schema` standing spec does NOT — it describes the schema file as a generated artifact. Regenerated via `make schema`; no manual spec delta needed.

**Care-point 3 (providers) verified:** `IProviderFactory` registrations (`OpenAiProviderFactory`, `AnthropicProviderFactory`, `GeminiProviderFactory`, `OllamaProviderFactory`) are wired in `AddDmonProviders()`, completely separate from `NuGetExtensionLoader`. Provider loading is unaffected.

**New tests (task 4.6) — `test/Dmon.Core.Tests/Hosting/DmonHostBuilderMiddlewareTests.cs` (7 tests):**

- `AddMiddleware_None_ApplyReturnsBaseClientUnchanged` — no middleware → bare client pass-through
- `AddMiddlewareT_NoOverride_MiddlewareLandsAtAttributePriority` — type registration at attribute priority
- `AddMiddlewareT_TwoTypes_LowerPriorityIsInnermost` — fold order from attribute priorities
- `AddMiddlewareT_RegistrationOverride_BeatsAttributePriority` — per-registration override reorders
- `AddMiddleware_EqualPriority_StableRegistrationOrderTiebreaker` — equal priority → registration order tiebreak
- `AddMiddlewareInstance_WithOverride_OverrideTakesPrecedenceOverAttribute` — instance overload with override
- `AddMiddlewareT_WithDiDependency_ConstructorInjectionWorks` — `ActivatorUtilities` resolves `IConfiguration` ctor param

**Gates: `make build` 0/0; full suite — 528 Core, 159 Terminal, 143 Gateway, 100 Protocol, 102 BuiltinTools, 51 Memory, 41 Omlx, 32 Providers, 26 Extensions, 15 Runtime (1 pre-existing skip across suite); 0 failures.**

## 5. Acquisition via SDK restore

- **5.1 — confirmed done (landed G3):** `NuGetCoreAcquisitionSource.cs` and `ICoreAcquisitionSource.cs` were deleted in Group 3 (commit `78475b2`). `CoreResolver` has no NuGet-cache or on-demand tier; the search confirmed zero remaining runtime-downloader types. Nothing to implement.
- **5.2 — pin guard + handshake-against-compiled-core already covered:**
  - Pin-drift guard: `CompositionRootTests.DefaultCoreDmonCs_ContainsCurrentProtocolPin` and `SampleComposedCoreDmonCs_ContainsCurrentProtocolPin` assert both `default-core/Dmon.cs` and `samples/Dmon.ComposedCore/Dmon.cs` contain `dmoncore@{ProtocolVersion.Current}.*` (derived string, repo-root located from assembly path).
  - Handshake gate against compiled core: `FileBasedProgramLaunchTests.FileBasedProgram_FirstBuild_FirstStdoutLineIsAgentReady` and `FileBasedProgram_Rebuild_AfterEdit_FirstStdoutLineIsAgentReady` spawn a real built `Dmon.cs` core and assert the first stdout line is `agentReady` with `protocolVersion`.
  - Nothing to implement.
- **5.3a — new: version-range restore test** (`VersionRangeRestoreTests.ProjectPinnedTo01Star_Restores019_NotVersion020`). Spec scenario: `Dmon.cs` pinning `dmoncore@0.1.*` with feed offering `0.1.3`, `0.1.9`, `0.2.0` must resolve `0.1.9`, never `0.2.0`.
  - **Stub approach chosen.** Packing the real `dmoncore` at `0.1.x` would require `MinVerVersionOverride=0.1.x`, conflicting with `pack-core.sh`'s documented requirement that `Major.Minor = ProtocolVersion.Current ("0.2")` so the skew guard passes. That path is outside `pack-core.sh`'s supported contract and irrelevant to what is being asserted. Instead: a minimal stub project with `<PackageId>dmoncore</PackageId>` is packed at `0.1.3`, `0.1.9`, and `0.2.0` into a unique Guid-named temp feed (torn down in `IAsyncLifetime.DisposeAsync`). The stub is correct because `#:package` is SDK syntactic sugar for `<PackageReference>`; both delegate to the same NuGet float-range resolution. A standard `.csproj` with `<PackageReference Include="dmoncore" Version="0.1.*" />` and an isolated `nuget.config` (pointing only at the temp feed) is used — `dotnet restore` on a regular `.csproj` writes `obj/project.assets.json` in the project directory, which is deterministically locatable. (File-based programs write their assets to `~/Library/Application Support/dotnet/runfile/<hash>/obj/`, where the hash depends on the file's absolute path — not statically knowable in a test.)
  - Test is fully self-contained: stubs are packed, restore runs, `project.assets.json` is read, resolved version extracted from `libraries["dmoncore/<ver>"]` key, asserted `"0.1.9"` and `Assert.NotEqual("0.2.0", ...)`.
- **5.3b — confirmed done:** `CompatibilityGateTests.ReadAgentReadyAsync_MismatchedProtocol_ThrowsProtocolMismatchException` (in `test/Dmon.Runtime.Tests/CompatibilityGateTests.cs`) covers protocol-mismatch rejection, and `ProtocolMismatchException_Message_IsActionable` covers the actionable-message requirement.
- **Gates:** `make build` 0/0; `make test` 529 Core (1 pre-existing skip) + full suite green; `openspec validate --strict` valid.

## 6. Config is settings-only

- Most of group 6 was already structural fact after G1/G4: no config extension *loader* remains, and `DmonHostBuilder` already layers `config.yaml` (global<project<local) into `builder.Configuration` with `WithModel` applied as an in-memory layer after the YAML so code wins. Group 6 finished the job.
- **6.1** removed the last residue of the extension list: dropped the scaffolded `# extensions:` / `.csx` comment block from `BootstrapService.DefaultConfig` (settings examples kept), and rewrote `docs/configuration.md` to delete the entire config-driven extension model (the `extensions` table row, "Extension configuration", "Extension loading — implementation notes", `extension.load`/`/load`/`extension.analyze`, first-writer-wins, ADR-008/009 loading narrative). **Kept** the settings docs: providers, `sessionStore`, session/retry settings, the `profiles` map, and the `commands:<name>` / `middleware:<ClassName>` self-settings sections (an extension/middleware reading its *own* settings via injected `IConfiguration` is retained — only config-driven *activation* is gone). Added a note that extensions/middleware are composed at compile time in `Dmon.cs`.
- **6.2 — Decision: added `DmonHostBuilder.ConfigureConfiguration(Action<IConfigurationManager>)`** as the read/override hook. `IConfigurationManager` lets the composition callback both read merged values (implements `IConfiguration`) and add/override sources (implements `IConfigurationBuilder`). Stored as a `List<...>` and invoked in `Build()` **last** — after the YAML layers and after the `WithModel` in-memory override — so the most-explicit code wins. Precedence (YAML < WithModel < hook) is XML-doc'd on the method. Considered (rejected) treating 6.2 as satisfied by `WithModel` alone: the spec says config "SHALL be exposed to `Dmon.cs` through the builder's configuration" and code "SHALL be able to read those settings," which the override-only path doesn't honour.
- **6.3** tests:
  - `Dmon.Core.Tests/Hosting/ConfigurationPrecedenceTests.cs` (new, `[Collection("FileSystemCwd")]`): `Build_WithModel_WinsOverConfigYamlActiveModel` writes a real `<cwd>/.dmon/config.yaml` `activeModel` and asserts `WithModel` wins via `IActiveModelStore.Load()`; plus three hook tests (read merged value; override wins over `WithModel`; multiple calls last-wins).
  - Repurposed the stale `BadConfigEntryIntegrationTest` → `Integration/LegacyExtensionsListIgnoredIntegrationTest.cs` (renamed file/class/method, doc rewritten): a legacy `extensions:` list in `config.yaml` is *ignored* (spawned core still emits `agentReady`) — the deleted fail-soft framing is gone.
- **Reviewer:** approved, no blockers. Nit (non-blocking, not actioned): the "ignored" integration test proves it only indirectly (a no-op key and a fail-soft load would both still emit `agentReady`); a stronger guard could assert stderr shows no loader attempt for the bogus source.
- **Gates:** `make build` 0/0; `make test` green (544 Core + 1 pre-existing skip; all suites pass); `openspec validate --strict` valid. Committed `08c60a1`.

## NEXT

- **Up next:** Task **1.3** (entry-point-less `dmoncore` library — remove the top-level program / confirm no `Main`; flip `OutputType`/`GenerateRuntimeConfigurationFiles`), then Group 8 (e2e + final gates).
- **Nits / deferred:**
  - **(7.3, reviewer arch-note):** the `[SkippableFact]` prereq-skip means a raw `dotnet test -c Release` (no `make`) skips the packaging tests instead of failing — CI could stay green while `build/dmoncore/` silently stops being produced. `test: build-core` mitigates `make test`; consider a CI-only env flag that converts these skips into hard failures for an airtight guarantee.
  - **(7.1, for the 1.3/G2 work):** `lib/net10.0/dmoncore.runtimeconfig.json` is emitted because the Worker SDK defaults `OutputType=Exe`. Inert for a library ref. Clean it up when 1.3 makes dmoncore truly entry-point-less (`OutputType=Library` / `GenerateRuntimeConfigurationFiles=false`).
  - `scripts/pack-core.sh` and `scripts/smoke-sdk.sh` share `.pack-out` (each `rm -rf`s it); harmless, both rebuild the trio.
  - (G1) `Build_WithProfile_ResolverReturnsOverriddenProfile` weakly discriminating; tighten when convenient.
- **Carry-forward:**
  - **Task 1.3** still open (entry-point-less library / no Main) — completes around G7 once packaging/`OutputType` flip.
  - Process model retained; only the launch *command* changed. `CoreSession`, stdio boundary, protocol gate, `RestartAsync` are all unchanged.
  - **Agent-facing extension discovery/vetting surface retained but load-path-orphaned:** `ExtensionSearchTool` (`extension.search`), `ExtensionReadmeTool` (`extension.readme`), and the `Extensions/Security/` subsystem (`ExtensionSourceFetcher`, `ExtensionSecurityAnalyser`, `SecurityReportFormatter`) remain registered in `BuiltinToolsRegistration.cs`. Their old `/load … /reload` workflow is deleted; they are now orphaned from any load path. Their tool descriptions likely still reference the deleted `/load` flow. **Deferred to a follow-up change** — outside this change's enumerated deletion surface; intersects the future ADR-021 `compose` permission tier and ADR-020 agent-definitions work. That follow-up should rework or retire these tools and their descriptions.
