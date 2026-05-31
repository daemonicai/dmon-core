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

### Requirement: Permission mode is selected per session from the active profile
The permission gate SHALL operate under a permission mode supplied by the active agent profile (the `agent-profiles` capability), one of `coding` or `sandbox`, resolved once per session. The `coding` mode SHALL preserve the existing permission behaviour unchanged. The mode SHALL be available to tool permission evaluators so that write/edit/delete evaluation can account for it.

#### Scenario: Coding mode preserves existing behaviour
- **WHEN** a session runs under permission mode `coding`
- **THEN** read, write, edit, delete, bash, and http evaluation behave exactly as before this change, with no implicit write allowance anywhere

#### Scenario: Mode is fixed for the session
- **WHEN** a session has resolved its permission mode at start
- **THEN** that mode applies to every tool call for the session's lifetime

### Requirement: Sandbox mode grants implicit write to the session asset directory
Under permission mode `sandbox`, write, edit, and delete operations whose normalised target path is within the session's own `assets/<session_id>/` subtree SHALL be implicitly allowed without a prompt (risk `none`), mirroring the implicit-read-within-CWD allowance. Operations outside that subtree SHALL be evaluated exactly as in `coding` mode. The hardcoded denylist SHALL continue to be checked before any allowance and SHALL NOT be overridable by `sandbox` mode.

#### Scenario: Write within the asset directory is implicitly allowed
- **WHEN** a session under `sandbox` mode writes a file whose normalised path is within `assets/<session_id>/`
- **THEN** the write is allowed without a prompt

#### Scenario: Write outside the asset directory still prompts
- **WHEN** a session under `sandbox` mode writes a file whose normalised path is outside `assets/<session_id>/` and not otherwise approved
- **THEN** the write is evaluated as in `coding` mode and a prompt is issued

#### Scenario: Denylist still applies under sandbox
- **WHEN** a session under `sandbox` mode issues an operation matching a denylist entry, even within the asset directory
- **THEN** the operation is denied unconditionally and the `sandbox` allowance does not override the denylist

