## ADDED Requirements

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
