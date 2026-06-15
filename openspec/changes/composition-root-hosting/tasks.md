## 1. Hosting surface (`DmonHost`)

- [x] 1.1 Add `Dmon.Hosting.DmonHost.CreateBuilder(args)` to the `dmoncore` library returning a builder (provider/model, extension registration, permission mode, profile) with `.Build()` → a host whose `RunAsync(cancellationToken)` runs today's JSONL/stdio core loop.
- [x] 1.2 Relocate the existing core `Program.Main`/bootstrap into `RunAsync()` so the wire contract (ADR-003), session storage (ADR-004), and permission pipeline (ADR-002/006) are byte-for-byte unchanged behind the new surface.
- [x] 1.3 Make `dmoncore` an entry-point-less **library** (remove its top-level program); confirm no `Main` remains in the package.
- [x] 1.4 Tests: a host built via `DmonHost.CreateBuilder(args).Build().RunAsync(ct)` emits `agentReady` and serves the same protocol as the prior stock core (golden-path RPC test).

## 2. `Dmon.cs` composition root, canonical default, and `dmon init`

- [x] 2.1 Author the **canonical `Dmon.cs`** (references `#:package dmoncore@<protocol>.*`, no extra extensions) that the prebuilt default core is published from.
- [x] 2.2 Implement `dmon init` to scaffold an editable `Dmon.cs` in the working directory.
- [x] 2.3 Provide a sample `Dmon.cs` that declares an extension `#:package` + builder `.AddExtension<…>()` to prove compile-time composition.
- [x] 2.4 Tests: `dmon init` produces a `Dmon.cs` that builds into a runnable core; a composed extension's tools are registered at startup with no runtime load step; contract types share one identity (single ALC graph).

## 3. `Dmon.Runtime` launch: precedence + build-then-`--no-build`-run

- [x] 3.1 Update core discovery precedence to `./Dmon.cs` > `--core-path`/`DMON_CORE_PATH` override > built-in prebuilt default; remove the NuGet-cache and on-demand-acquisition tiers.
- [x] 3.2 Implement the two-step launch via `ICoreLauncher`/`ICoreProcess`: `dotnet build Dmon.cs` as a separate process (stdout/stderr captured; incremental build is the staleness gate), then `dotnet run Dmon.cs --no-build` as the stdio child. `dotnet exec` the prebuilt assembly directly for the default/override path.
- [x] 3.3 Ensure `/reload` rebuilds incrementally and re-runs `Dmon.cs` (restart-between-turns); a no-change reload is a near-no-op.
- [x] 3.4 Tests: the `Dmon.cs` child's stdout carries only JSONL frames (no build/restore output) on first build and on a rebuild-triggering `/reload`; precedence resolves `Dmon.cs` over override over default; empty dir uses the prebuilt default with no SDK/network. (Confirm `--no-build` stdout silence for the file-based-program path; fall back to exec-the-built-dll if needed — design OQ-B.)

## 4. Delete the dynamic-load tier

- [x] 4.1 Remove the `.csx` loader and the `Dotnet.Script.Core` dependency.
- [x] 4.2 Remove the `config.yaml` extension loader: the user/project union, load-at-startup, per-entry fail-soft, and the `promote` path.
- [x] 4.3 Remove `AssemblyLoadContext` reflection discovery and `AssemblyDependencyResolver` probing (extensions are compiled in).
- [x] 4.4 Retire the `extension.load` / `extension.unload` / `extension.list` RPC commands and `ExtensionUnloadedEvent`. (Flag any `protocol-schema` spec impact for a follow-up delta — design OQ-C.)
- [x] 4.5 Confirm retained: `IDmonExtension`/`AIFunction`, `IDmonMiddleware`/`DmonMiddlewareAttribute`, and the permission pipeline. Middleware is now registered via the builder, not reflection-discovered.
- [x] 4.6 Add a `DmonHost` builder middleware-registration surface (`AddMiddleware<T>()` + instance overload, optional priority override) and fold registered middleware by priority; drop the config-driven `middleware:` activation/priority section (middleware may still read its own settings via `IConfiguration`). Land the `specs/extension-middleware` delta (reflection-discovery + YAML-section requirements removed/modified; builder-registration requirement added).

## 5. Acquisition via SDK restore

- [x] 5.1 Remove dmon's runtime NuGet downloader; acquisition of `dmoncore` + extensions is `dotnet restore` over the `Dmon.cs` `#:package` set (SDK-driven, build-time).
- [x] 5.2 Express the protocol pin as `#:package dmoncore@<Major.Minor>.*` (= `ProtocolVersion.Current`); keep the `agentReady` `protocolVersion` handshake gate firing against the compiled core.
- [x] 5.3 Tests: a `Dmon.cs` pinning `dmoncore@0.1.*` restores the newest `0.1.x` and never `0.2.0`; a protocol-mismatched core is rejected at handshake.

## 6. Config is settings-only

- [x] 6.1 Strip the `extensions` list from `config.yaml` handling; retain settings and expose them through the builder configuration.
- [x] 6.2 Composition code can read or override config (e.g. pin a provider/model in `Dmon.cs` that wins over `config.yaml`).
- [x] 6.3 Tests: code-set provider wins over a conflicting `config.yaml`; a legacy `extensions:` list is ignored for composition.

## 7. Packaging

- [x] 7.1 Publish `dmoncore` as a **library** package (`#:package`-able; deps as package references, not a runnable closure); update `IsPackable`/metadata accordingly.
- [x] 7.2 Produce the **prebuilt default-core** artifact (a publish closure of the canonical `Dmon.cs`) runnable via `dotnet exec` with no SDK/restore, and **bundle it into the `dmon` tool package** as a file payload (no NuGet dependency on `dmoncore`) so first run works offline with no SDK (design D3 / package-publishing).
- [x] 7.3 Tests/packaging checks: the `dmoncore` package is a referenceable library; the prebuilt default unpacks to a runnable closure (`dmoncore.dll` + deps + `runtimeconfig.json`).

## 8. End-to-end and spec validation

- [x] 8.1 End-to-end: from an empty dir the prebuilt default serves a turn; after `dmon init` + adding an extension `#:package`, a build-then-`--no-build`-run launch serves a turn with that extension's tools and a clean JSONL stdout; `/reload` after an edit rebuilds and restarts.
- [x] 8.2 `make build` clean (`TreatWarningsAsErrors`), `make test` green (new + existing), `openspec validate composition-root-hosting --strict` passes.
