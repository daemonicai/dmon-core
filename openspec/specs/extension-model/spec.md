## Purpose

Defines the extension model: the `IToolExtension`/`AIFunction` tool contract and its self-evaluation hooks, how tool extensions are composed into the host via the `AddToolExtension` verb and DI-constructed (`ActivatorUtilities.CreateInstance`), how they are discovered by build-time DI-enumeration, and the restart-to-reload semantics. All author-facing contracts live in `Dmon.Abstractions` (the `Dmon.Extensions` package is deleted).
## Requirements
### Requirement: Dmon.Abstractions exports the tool, middleware, and facet contracts
The `Dmon.Abstractions` assembly SHALL export the renamed tool contract `IToolExtension` (formerly `IDmonExtension`), `IDmonMiddleware`, `DmonMiddlewareAttribute`, `DmonAIFunctionFactory`, the three registration facets (`IProviderRegistration`, `IToolRegistration`, `IMiddlewareRegistration`), `IDmonHostBuilder`, and `IChatClientFactory` as public types in a single root namespace. The `Dmon.Extensions` package SHALL be deleted; all author-facing contracts collapse into `Dmon.Abstractions`. The published SDK base SHALL become `Dmon.Protocol` + `Dmon.Abstractions` + `dmoncore`. This is a clean break — no back-compat is required (no production deployments).

#### Scenario: Extension package references only Dmon.Abstractions
- **WHEN** an extension NuGet package references only `Dmon.Abstractions`
- **THEN** it has access to `IToolExtension`, `IDmonMiddleware`, and the registration facets without additional references

#### Scenario: Dmon.Extensions no longer exists
- **WHEN** the package set is inspected after this change
- **THEN** there is no `Dmon.Extensions` package, and its former public types resolve from `Dmon.Abstractions`

### Requirement: IToolExtension provides self-evaluation via Evaluate
`IToolExtension` (formerly `IDmonExtension`) SHALL declare a method `PermissionResult Evaluate(FunctionCallContent call, IPermissionSettings project, IPermissionSettings? global)` with a default implementation that returns `PermissionResult.Prompt`. Tool extensions that understand their own argument shapes and risk profile SHALL override this method to return `Allow`, `Prompt`, or `Deny` without requiring tool-category dispatch in the gate. The contract shape (`Name`, `Description`, `IEnumerable<AIFunction> Tools`, `Evaluate`, `CreateConfirmRequest`) SHALL be unchanged by the rename; only the interface name changes.

#### Scenario: Default implementation returns Prompt
- **WHEN** a tool extension does not override `Evaluate`
- **THEN** `extension.Evaluate(call, projectSettings, globalSettings)` returns `PermissionResult.Prompt`

#### Scenario: BashTool override returns Deny for denylist commands
- **WHEN** `BashTool.Evaluate` is called with a `command` argument matching the hardcoded denylist
- **THEN** it returns `PermissionResult.Deny`

#### Scenario: ReadFileTool override returns Allow for paths within CWD
- **WHEN** `ReadFileTool.Evaluate` is called with a `path` argument inside the current working directory
- **THEN** it returns `PermissionResult.Allow`

### Requirement: IToolExtension provides CreateConfirmRequest with default implementation
`IToolExtension` (formerly `IDmonExtension`) SHALL declare a method `ToolConfirmRequest CreateConfirmRequest(FunctionCallContent call)` with a default implementation that produces a request using `call.CallId`, `call.Name`, `call.Arguments`, and `RiskLevel.Low`. Tool extensions SHALL override this to supply an accurate `RiskLevel` and any additional context.

#### Scenario: Default CreateConfirmRequest uses Low risk
- **WHEN** a tool extension does not override `CreateConfirmRequest`
- **THEN** the returned `ToolConfirmRequest.Risk` equals `RiskLevel.Low`

#### Scenario: WriteFileTool override uses High risk
- **WHEN** `WriteFileTool.CreateConfirmRequest` is called
- **THEN** the returned `ToolConfirmRequest.Risk` equals `RiskLevel.High`

### Requirement: IToolRegistry exposes FindExtension reverse lookup
`IToolRegistry` SHALL declare `IToolExtension? FindExtension(string toolName)` returning the `IToolExtension` that registered the named tool, or null if no such tool is registered.

#### Scenario: Known tool name returns owning extension
- **WHEN** `FindExtension("bash")` is called after built-in tools are registered
- **THEN** it returns the `BashTool` extension instance

#### Scenario: Unknown tool name returns null
- **WHEN** `FindExtension("nonexistent_tool")` is called
- **THEN** it returns null

### Requirement: Register signature includes the IToolExtension instance
`IToolRegistry.Register` SHALL accept the owning `IToolExtension` instance so that `FindExtension` can return it. The updated signature SHALL be `Register(string extensionName, IToolExtension extension, IEnumerable<AIFunction> tools)`.

#### Scenario: Tools registered via new signature are retrievable
- **WHEN** `Register("myext", extension, tools)` is called and then `FindExtension(tool.Name)` is called for a tool in that list
- **THEN** the original `extension` instance is returned

### Requirement: Tool extensions are registered via AddToolExtension and DI-constructed
Tool extensions SHALL be registered via the `AddToolExtension<T>()` verb (replacing the former `AddExtension<T>()`), which SHALL register the type as a singleton `IToolExtension` for build-time DI-discovery. `AddToolExtension<T>` SHALL drop the `new()` constraint and instantiate the tool via `ActivatorUtilities.CreateInstance`, so a tool MAY inject host services through its constructor. An instance overload (`AddToolExtension(IToolExtension)`) SHALL also be supported.

#### Scenario: Tool is constructed via ActivatorUtilities with injected services
- **WHEN** a tool extension declares a constructor parameter for a host service and is registered with `.AddToolExtension<MyTool>()`
- **THEN** the host resolves the service from the container and constructs the tool via `ActivatorUtilities.CreateInstance`, with no `new()` constraint required

#### Scenario: AddExtension is replaced by AddToolExtension
- **WHEN** a composition root that previously called `.AddExtension<T>()` is updated
- **THEN** it calls `.AddToolExtension<T>()`, registering `T` as a singleton `IToolExtension`
