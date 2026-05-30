## Purpose

Define the current-state permission model for dmon: how `IPermissionPolicy` exposes project and global settings to extension evaluators, how `PermissionGateChatClient` delegates per-tool permission evaluation to the owning extension, and how `BashTool` inlines denylist and composite command detection.

## Requirements

### Requirement: IPermissionPolicy provides settings access
`IPermissionPolicy` SHALL expose `IPermissionSettings ProjectSettings` and `IPermissionSettings? GlobalSettings` so that extension `Evaluate` implementations can read the permission configuration. The interface SHALL NOT contain any method that names a tool category.

#### Scenario: Gate retrieves settings for extension evaluation
- **WHEN** `PermissionGateChatClient` evaluates a tool call
- **THEN** it calls `IPermissionPolicy.ProjectSettings` and `IPermissionPolicy.GlobalSettings` to pass to `extension.Evaluate`

#### Scenario: IPermissionPolicy has no EvaluateRead, EvaluateWrite, EvaluateBash, or EvaluateHttp
- **WHEN** `IPermissionPolicy` is inspected
- **THEN** none of the methods `EvaluateRead`, `EvaluateWrite`, `EvaluateBash`, or `EvaluateHttp` exist on the interface

### Requirement: PermissionGateChatClient delegates evaluation to the owning extension
`PermissionGateChatClient` SHALL call `IToolRegistry.FindExtension(call.Name)` to locate the owning extension, then call `extension.Evaluate(call, policy.ProjectSettings, policy.GlobalSettings)` to obtain a `PermissionResult`. If no extension is found for the tool name, the gate SHALL default to `PermissionResult.Prompt`.

#### Scenario: Known tool evaluated by its extension
- **WHEN** the LLM calls a built-in tool such as `bash`
- **THEN** the gate calls `BashTool.Evaluate` and acts on the returned `PermissionResult`

#### Scenario: Unknown tool defaults to Prompt
- **WHEN** the LLM calls a tool whose name does not match any registered extension
- **THEN** the gate treats the result as `PermissionResult.Prompt`

### Requirement: BashTool inlines composite detection and denylist logic
`BashTool.Evaluate` SHALL contain the denylist check (previously in `DenylistChecker`) and composite command detection (previously in `BashCompositeDetector`) inline. The `IBashCompositeDetector` and `IDenylistChecker` implementations SHALL move to `Daemon.BuiltinTools`. Their interfaces MAY remain in `Daemon.Core` if other components still reference them, or move with their implementations if no other references exist.

#### Scenario: Denylist command returns Deny without Prompt
- **WHEN** `BashTool.Evaluate` receives a `command` argument matching a denylist entry
- **THEN** it returns `PermissionResult.Deny` immediately, never `Prompt`

#### Scenario: Composite command returns Prompt
- **WHEN** `BashTool.Evaluate` receives a `command` that chains multiple sub-commands (e.g. using `&&`, `||`, `;`, or pipe)
- **THEN** it returns `PermissionResult.Prompt`

#### Scenario: Simple safe command within CWD may return Allow
- **WHEN** `BashTool.Evaluate` receives a simple, non-composite command whose path is within the current working directory and does not match the denylist
- **THEN** it MAY return `PermissionResult.Allow` according to the project permission settings
