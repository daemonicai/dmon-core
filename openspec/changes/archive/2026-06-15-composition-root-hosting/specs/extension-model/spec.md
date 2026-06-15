## REMOVED Requirements

### Requirement: Extensions load into the Default AssemblyLoadContext
**Reason**: ADR-019 removes the runtime extension loader. Extensions are now compile-time `#:package` references in `Dmon.cs`, compiled into the core by the SDK — there is no runtime `.dll`-path load and no reflection discovery. The retained ADR-008 principle (single shared context, restart-as-reclaim) is re-asserted in the new `composition-root-hosting` capability, where it holds trivially: a compiled program is one SDK-resolved identity graph in the Default ALC.
**Migration**: Declare the extension as a `#:package` (or `#:project`/`#:ref`) in `Dmon.cs` and register it with a builder call; rebuild and reload.

### Requirement: Extension transitive dependencies are resolved
**Reason**: ADR-019 delegates dependency resolution to `dotnet restore`/build over the `#:package` set; the runtime `AssemblyDependencyResolver` + `.deps.json`-probing path no longer exists.
**Migration**: Transitive dependencies resolve at build time as for any dotnet project; no runtime action.

### Requirement: Conflicting dependency versions are not supported
**Reason**: Version conflicts are now surfaced by NuGet at restore/build time, not by a runtime first-writer-wins policy.
**Migration**: Resolve version conflicts in `Dmon.cs`'s `#:package` set (the build reports them).

### Requirement: Unload deregisters tools without reclaiming assemblies
**Reason**: ADR-019 removes runtime extension management. Extensions are part of the compiled composition; deactivating one means editing `Dmon.cs` and reloading (restart-between-turns), not an `extension.unload` command.
**Migration**: Remove the extension's `#:package`/builder call from `Dmon.cs` and reload.

### Requirement: Script loading uses no dedicated load context
**Reason**: ADR-019 removes the `.csx` tier and the `Dotnet.Script.Core` dependency entirely.
**Migration**: Author the extension as a compiled package/project referenced from `Dmon.cs`; mid-session `.csx` becomes edit-`Dmon.cs`-then-reload.

### Requirement: Extensions are declared in config at user and project scope
**Reason**: ADR-019 supersedes ADR-009 in full — composition is code (`Dmon.cs`), not a `config.yaml` extension list. `config.yaml` is retained for settings only.
**Migration**: Move each `extensions:` entry to a `#:package` + builder call in `Dmon.cs`.

### Requirement: Effective extension set is the deduplicated union of both scopes
**Reason**: The config-driven extension list is retired (ADR-009 superseded); there is no user/project union to compute.
**Migration**: The effective set is whatever `Dmon.cs` declares; shared wiring is factored via `#:include`/`#:ref` (ADR-020 follow-up).

### Requirement: Config-declared extensions load at startup without prompting
**Reason**: There is no config-declared set to load; extensions are compiled in. (The trust gate on the agent editing `Dmon.cs` is the ADR-021 `compose` tier — a separate change.)
**Migration**: None — compiled-in extensions are present by construction at startup.

### Requirement: There is no ephemeral runtime-load tier
**Reason**: Reaffirmed in spirit by ADR-019 but the requirement's mechanism (add source to `config.yaml` + reload) is gone. The replacement — extensions are compile-time, activation is editing `Dmon.cs` + rebuild/reload — is asserted in `composition-root-hosting`.
**Migration**: Edit `Dmon.cs` and reload to change the extension set.

### Requirement: A failing config extension does not abort startup
**Reason**: Per-entry fail-soft no longer applies: a broken extension is a compile error that fails the `Dmon.cs` build as a whole (fail-closed). Build/launch-failure handling moves to `core-runtime-acquisition`.
**Migration**: Fix the build error in `Dmon.cs` (the build reports it); the prebuilt default core remains launchable meanwhile.

### Requirement: Extension loader performs middleware discovery pass
**Reason**: ADR-019 removes reflection discovery. Middleware is contributed through the `DmonHost` builder at compile time, not discovered at load.
**Migration**: Register middleware via the builder in `Dmon.cs`; the `IDmonMiddleware`/`DmonMiddlewareAttribute` contract is unchanged.
