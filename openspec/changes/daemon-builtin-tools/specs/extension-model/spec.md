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
