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

## NEXT

- **Up next:** Group 2 — `Dmon.cs` composition root, canonical default, `dmon init`. This is where the **dev-feed** question must be resolved.
- **Open questions:**
  - **G2/G3 dev-feed (blocking G2):** building a `Dmon.cs` that does `#:package dmoncore@<protocol>.*` needs a dev acquisition path since `dmoncore` isn't on nuget.org — local NuGet feed fed by a local `dotnet pack`, or `#:project`/`#:ref` for the in-repo canonical root. Decide at G2 start.
- **Nits / deferred:**
  - Reviewer nit (non-blocking): `Build_WithProfile_ResolverReturnsOverriddenProfile` is weakly discriminating (bare resolver also falls back to `coding`); fix verified by inspection + the override decorator. Tighten with a fake inner resolver capturing `requestedProfile` when convenient.
- **Carry-forward:** Process model retained (core stays a spawned JSONL/stdio child via `ICoreLauncher`/`ICoreProcess`); only the launch *command* changes (G3). `DmonBuiltHost.Services` was added to allow post-`Build()` service resolution (used by tests / composition roots).
