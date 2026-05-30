## Purpose

Defines the extension model: the `IDmonExtension`/`AIFunction` tool contract and its self-evaluation hooks, how extensions are loaded into the Default `AssemblyLoadContext` and their dependencies resolved, how they are declared in `config.yaml` and loaded at startup (config presence implies trust; the security gate fires at add time), and the deregister-only unload and restart-to-reload semantics.
## Requirements
### Requirement: IDaemonExtension provides self-evaluation via Evaluate
`IDaemonExtension` SHALL declare a method `PermissionResult Evaluate(FunctionCallContent call, IPermissionSettings project, IPermissionSettings? global)` with a default implementation that returns `PermissionResult.Prompt`. Extensions that understand their own argument shapes and risk profile SHALL override this method to return `Allow`, `Prompt`, or `Deny` without requiring tool-category dispatch in the gate.

#### Scenario: Default implementation returns Prompt
- **WHEN** an extension does not override `Evaluate`
- **THEN** `extension.Evaluate(call, projectSettings, globalSettings)` returns `PermissionResult.Prompt`

#### Scenario: BashTool override returns Deny for denylist commands
- **WHEN** `BashTool.Evaluate` is called with a `command` argument matching the hardcoded denylist
- **THEN** it returns `PermissionResult.Deny`

#### Scenario: ReadFileTool override returns Allow for paths within CWD
- **WHEN** `ReadFileTool.Evaluate` is called with a `path` argument inside the current working directory
- **THEN** it returns `PermissionResult.Allow`

### Requirement: IDaemonExtension provides CreateConfirmRequest with default implementation
`IDaemonExtension` SHALL declare a method `ToolConfirmRequest CreateConfirmRequest(FunctionCallContent call)` with a default implementation that produces a request using `call.CallId`, `call.Name`, `call.Arguments`, and `RiskLevel.Low`. Extensions SHALL override this to supply an accurate `RiskLevel` and any additional context.

#### Scenario: Default CreateConfirmRequest uses Low risk
- **WHEN** an extension does not override `CreateConfirmRequest`
- **THEN** the returned `ToolConfirmRequest.Risk` equals `RiskLevel.Low`

#### Scenario: WriteFileTool override uses High risk
- **WHEN** `WriteFileTool.CreateConfirmRequest` is called
- **THEN** the returned `ToolConfirmRequest.Risk` equals `RiskLevel.High`

### Requirement: Existing extensions remain binary-compatible
The default implementations on `IDaemonExtension` SHALL ensure that existing compiled extensions (`.csx` scripts and NuGet packages) continue to work without recompilation. They will silently use `Prompt` / `Low` risk defaults.

#### Scenario: Extension compiled before this change continues to load
- **WHEN** an extension compiled against the previous `IDaemonExtension` contract (without `Evaluate` or `CreateConfirmRequest`) is loaded
- **THEN** the extension loads without error and its tools are available

### Requirement: IToolRegistry exposes FindExtension reverse lookup
`IToolRegistry` SHALL declare `IDaemonExtension? FindExtension(string toolName)` returning the `IDaemonExtension` that registered the named tool, or null if no such tool is registered.

#### Scenario: Known tool name returns owning extension
- **WHEN** `FindExtension("bash")` is called after built-in tools are registered
- **THEN** it returns the `BashTool` extension instance

#### Scenario: Unknown tool name returns null
- **WHEN** `FindExtension("nonexistent_tool")` is called
- **THEN** it returns null

### Requirement: Register signature includes the IDaemonExtension instance
`IToolRegistry.Register` SHALL accept the owning `IDaemonExtension` instance so that `FindExtension` can return it. The updated signature SHALL be `Register(string extensionName, IDaemonExtension extension, IEnumerable<AIFunction> tools)`.

#### Scenario: Tools registered via new signature are retrievable
- **WHEN** `Register("myext", extension, tools)` is called and then `FindExtension(tool.Name)` is called for a tool in that list
- **THEN** the original `extension` instance is returned

### Requirement: Daemon.Extensions references Daemon.Protocol
The `Daemon.Extensions` project SHALL add a project reference to `Daemon.Protocol` so that `IDaemonExtension.Evaluate` can accept `IPermissionSettings` and return `PermissionResult`, and `CreateConfirmRequest` can return `ToolConfirmRequest`.

#### Scenario: IDaemonExtension types compile
- **WHEN** `Daemon.Extensions` is built after adding the `Daemon.Protocol` reference
- **THEN** the build succeeds with zero warnings

### Requirement: Extensions load into the Default AssemblyLoadContext
The extension loader SHALL load NuGet and local-assembly extensions into `AssemblyLoadContext.Default`. It SHALL NOT create per-load collectible `AssemblyLoadContext` instances for extensions, and SHALL NOT hold or `Unload()` an extension load context.

#### Scenario: Local assembly loads into the Default context
- **WHEN** an extension is loaded from a local `.dll` path
- **THEN** the assembly is loaded into `AssemblyLoadContext.Default`
- **AND** no collectible `AssemblyLoadContext` is created for it

#### Scenario: Extension contract types share host identity
- **WHEN** an extension assembly implementing `IDmonExtension` is loaded
- **THEN** the loaded type's `IDmonExtension` resolves to the same `Type` as the host's
- **AND** reflection discovery registers the extension's tools

#### Scenario: Loading a second extension does not disturb the first
- **WHEN** extension A is loaded and then extension B is loaded
- **THEN** extension A's tools remain registered and callable
- **AND** no `Unload()` is invoked on any context as a result of loading B

### Requirement: Extension transitive dependencies are resolved
The extension loader SHALL resolve an extension's transitive dependencies. For each loaded extension assembly it SHALL consult an `AssemblyDependencyResolver` built from the extension assembly's path (reading its `.deps.json`), and SHALL fall back to probing the extension assembly's own directory when no `.deps.json` entry matches.

#### Scenario: Extension with a sibling dependency loads
- **WHEN** an extension assembly depends on another assembly present in its own directory
- **THEN** the dependency resolves and the extension loads without a missing-assembly error

#### Scenario: Extension with a deps.json-described dependency loads
- **WHEN** an extension assembly ships a `.deps.json` describing a dependency
- **THEN** the loader resolves that dependency via the dependency resolver

### Requirement: Conflicting dependency versions are not supported
With all extensions sharing the Default context, the loader SHALL use a first-writer-wins policy for dependency versions: once a version of an assembly is loaded, subsequent extensions requiring a different version receive the loaded version or fail to load.

#### Scenario: Second extension requesting a different version gets the loaded one
- **WHEN** extension A has loaded version X of a dependency and extension B requires version Y of the same dependency
- **THEN** extension B resolves against version X (no second version is loaded)

### Requirement: Unload deregisters tools without reclaiming assemblies
`extension.unload <name>` SHALL remove the named extension's tools from the tool registry and emit `ExtensionUnloadedEvent`. It SHALL NOT unload or reclaim the extension's assemblies; those remain resident in the process until the process exits. Reclaiming extension code is achieved by restarting the core process.

#### Scenario: Unloaded tools stop being offered
- **WHEN** `extension.unload <name>` is invoked for a registered extension
- **THEN** that extension's tools are removed from the registry and no longer offered to the LLM
- **AND** an `ExtensionUnloadedEvent` is emitted

#### Scenario: Re-loading after unload does not require new type identity
- **WHEN** an extension is unloaded and then the same assembly is loaded again in the same process
- **THEN** loading succeeds using the already-resident assembly

### Requirement: Script loading uses no dedicated load context
The `.csx` script loader SHALL NOT create or hold an `AssemblyLoadContext` of its own. Script compilation and assembly loading remain delegated to Dotnet.Script.

#### Scenario: Script extension loads and returns tools
- **WHEN** a `.csx` script that returns one or more `AIFunction` instances is loaded
- **THEN** its tools are registered
- **AND** the loader holds no `AssemblyLoadContext` reference for the script

### Requirement: Extensions are declared in config at user and project scope
`config.yaml` SHALL support an `extensions` list at both the project scope (`./.dmon/config.yaml`) and the user scope (`~/.dmon/config.yaml`). Each entry SHALL carry a `source` (a `nuget:` id, an assembly path, or a `.csx` path) and MAY carry optional per-entry settings.

#### Scenario: Project and user extensions are both recognized
- **WHEN** the project `config.yaml` lists source A and the user `config.yaml` lists source B
- **THEN** both A and B are part of the effective extension set

#### Scenario: Absent extensions key yields an empty set
- **WHEN** neither config file contains an `extensions` key
- **THEN** the effective extension set is empty and startup proceeds normally

### Requirement: Effective extension set is the deduplicated union of both scopes
At startup the daemon SHALL compute the effective extension set as the union of the user and project `extensions` lists, deduplicated by normalized source. The union SHALL be computed by reading both files explicitly, not via configuration array layering. Where the same source appears at both scopes, the project entry's per-entry settings SHALL win.

#### Scenario: Same source in both scopes loads once
- **WHEN** the same normalized source appears in both the user and project lists
- **THEN** the extension is loaded exactly once
- **AND** the project entry's per-entry settings are used

#### Scenario: Load order is deterministic
- **WHEN** the effective set is loaded
- **THEN** user entries are loaded before project entries, each in file order

### Requirement: Config-declared extensions load at startup without prompting
Extensions present in config SHALL be loaded at `dmoncore` startup without an interactive permission prompt. Config presence is the prior approval (the concrete meaning of an extension source approved at project/user scope). The permission/security gate SHALL instead apply when a source is added to config.

#### Scenario: Startup loads config extensions silently
- **WHEN** `dmoncore` starts with a non-empty effective extension set
- **THEN** each extension is loaded and its tools registered
- **AND** no interactive load confirmation is requested for those entries

### Requirement: There is no ephemeral runtime-load tier
The daemon SHALL NOT support loading an extension into the running process without it being present in config. Activating a source SHALL require writing it to a config scope and reloading; removing a source SHALL require deleting it from config. A core operation MAY append a source to a chosen scope's `extensions` list (running the add-time gate before writing), but SHALL report that a reload is required rather than loading into the running process.

#### Scenario: Adding a source writes config and requires reload
- **WHEN** an extension source is added via the core add operation
- **THEN** the source is written to the chosen config scope's `extensions` list
- **AND** the response indicates a reload is required to activate it

#### Scenario: Removing a source from config deactivates it on next reload
- **WHEN** a source is removed from config and the core is reloaded
- **THEN** that extension is not part of the effective set and its tools are not registered

### Requirement: A failing config extension does not abort startup
If an extension declared in config fails to load, the daemon SHALL log the failure for that entry and continue loading the remaining extensions and starting normally.

#### Scenario: One bad entry is skipped
- **WHEN** one config-declared extension fails to load and others succeed
- **THEN** the failing extension is skipped with a logged error
- **AND** the remaining extensions load and the daemon starts

### Requirement: Dmon.Extensions exports IDmonMiddleware and DmonMiddlewareAttribute
The `Dmon.Extensions` assembly SHALL export `IDmonMiddleware` and `DmonMiddlewareAttribute` as public types. These SHALL be in the same root namespace as `IDmonExtension` and `DmonAIFunctionFactory`. No existing public types SHALL be removed or renamed by this change.

#### Scenario: Extension package references only Dmon.Extensions
- **WHEN** an extension NuGet package references only `Dmon.Extensions`
- **THEN** it has access to both `IDmonExtension` and `IDmonMiddleware` without additional references

#### Scenario: Existing extensions remain binary-compatible
- **WHEN** an extension compiled against the previous `Dmon.Extensions` (without middleware types) is loaded
- **THEN** it loads without error and its tools are available

### Requirement: Extension loader performs middleware discovery pass
The extension loader SHALL perform a middleware discovery pass after the existing tool discovery pass. The two passes are independent: a single extension assembly may expose both tools and middleware. Results of both passes are merged before the pipeline is constructed.

#### Scenario: Assembly with both tools and middleware contributes both
- **WHEN** an extension assembly contains an `IDmonExtension` tool implementation and an `IDmonMiddleware` implementation
- **THEN** the loader registers the tools and adds the middleware to the pipeline

#### Scenario: Tool-only assembly is unaffected by middleware pass
- **WHEN** an extension assembly contains only `IDmonExtension` implementations
- **THEN** the middleware discovery pass finds nothing and produces no error

