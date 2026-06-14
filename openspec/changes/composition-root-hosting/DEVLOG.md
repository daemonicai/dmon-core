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

## 7. Packaging

*(Task 7.1 pulled ahead of Group 2 — G2's "compose a Dmon.cs against `#:package dmoncore`" hard-depends on dmoncore being a library package. 7.2/7.3 remain in the G7 slot.)*

- **7.1 done** (committed standalone): `dmoncore` (`src/Dmon.Core`) now packs as a **library** package. `Dmon.Core.csproj`: set `<PackageId>dmoncore</PackageId>`; removed the root-closure machinery (`_AddPublishClosureToPackage` target, `TargetsForTfmSpecificContentInPackage`/`_PublishClosureStageDir`, `IncludeBuildOutput=false`, `IncludeSymbols=false`, `SuppressDependenciesWhenPacking`, `NU5100;NU5128` NoWarn) so it produces `lib/net10.0/dmoncore.dll`.
- **Decision (user):** the three **non-packable** impl refs (`Dmon.BuiltinTools`, `Dmon.Providers`, `Dmon.Providers.Ollama`) are **bundled** into `lib/net10.0/` via a `CopyImplDepsToPackage` target (`TargetsForTfmSpecificBuildOutput`, filtered by exact filename, `PrivateAssets="all"` on those refs). The packable contract trio (`Protocol`/`Abstractions`/`Extensions`) stay as package **dependencies**. The bundled DLLs' external NuGet deps were declared on dmoncore: added `Anthropic` 12.24.1, `GeminiDotnet.Extensions.AI` 0.25.0, `Microsoft.Extensions.AI.OpenAI` 10.5.1, `OllamaSharp` 5.4.25.
- **Decision (user):** dev acquisition = local `.pack-out` feed (existing `smoke-sdk.sh` precedent). New `scripts/pack-core.sh` packs the contract trio + dmoncore at a **stable** `0.2.0` (`-p:MinVerVersionOverride=0.2.0`) so a spec-literal `#:package dmoncore@0.2.*` resolves (untagged MinVer would yield a prerelease `0.2.*` won't match). Skew guard (`CheckProtocolVersionSkew`) passes legitimately — the override sets MinVerMajor/Minor=0/2 == `ProtocolVersion.Current`.
- Verified empirically (worker + reviewer re-packed): nupkg `lib/` has exactly the 4 expected DLLs; nuspec deps complete; a throwaway `Dmon.cs` with `#:package dmoncore@0.2.*` compiles against the feed. `dotnet publish` (→ `make build`) is unaffected; full suite green (1011 tests).

## NEXT

- **Up next:** Group 3 — `Dmon.Runtime` launch precedence + build-then-`--no-build`-run. Update core discovery to `./Dmon.cs` > `--core-path`/`DMON_CORE_PATH` > prebuilt default (remove NuGet-cache/on-demand tiers); two-step launch via `ICoreLauncher`/`ICoreProcess` (`dotnet build Dmon.cs` captured, then `dotnet run Dmon.cs --no-build`), `dotnet exec` the prebuilt dll for default/override; `/reload` rebuilds incrementally + restarts. **OQ-B to confirm empirically:** is `dotnet run --no-build` stdout truly silent (only JSONL frames) for a file-based program? Fall back to exec-the-built-dll if not. This is where the **stale root-layout** `CoreResolver`/`NuGetCoreAcquisitionSource` paths (now pointing at root, not `lib/`) get reworked — coordinate with G5's removal.
- **Open questions:**
  - **OQ-B (G3):** `dotnet run Dmon.cs --no-build` stdout purity for file-based programs — confirm at implementation; `--verbosity quiet` or exec-the-built-dll as fallback.
- **Nits / deferred:**
  - **(7.1, for the 1.3/G2 work):** `lib/net10.0/dmoncore.runtimeconfig.json` is emitted because the Worker SDK defaults `OutputType=Exe`. Inert for a library ref. Clean it up when 1.3 makes dmoncore truly entry-point-less (`OutputType=Library` / `GenerateRuntimeConfigurationFiles=false`).
  - `scripts/pack-core.sh` and `scripts/smoke-sdk.sh` share `.pack-out` (each `rm -rf`s it); harmless, both rebuild the trio.
  - (G1) `Build_WithProfile_ResolverReturnsOverriddenProfile` weakly discriminating; tighten when convenient.
- **Carry-forward:**
  - **Group 5 MUST replace, not just delete:** `Dmon.Runtime/CoreResolver.cs` (~L124-127) and `NuGetCoreAcquisitionSource.cs` (~L53) still resolve `dmoncore.dll` at the package **root** — now under `lib/`. G5 removes these download-and-exec paths; ensure the launch story (G3) consumes the library via `Dmon.cs` build/run instead.
  - **Task 1.3** still open (entry-point-less library / no Main) — completes around G2/G7 once the canonical `Dmon.cs` is the entry point and packaging/`OutputType` flip.
  - Process model retained; only the launch *command* changes (G3). `DmonBuiltHost.Services` exists for post-`Build()` resolution.
