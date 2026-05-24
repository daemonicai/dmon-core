## Why

daemon has no tools — the turn loop, permission gate, and tool registry are all wired up, but there is nothing for the LLM to call. Without Read, Write, Edit, Bash, Fetch, and Glob the agent is a chat wrapper. Introducing the built-in tool suite also reveals that the current `IDaemonExtension` / `IPermissionPolicy` split is inside-out: the policy evaluates tool calls using hard-coded knowledge of tool categories, when evaluation logically belongs to the extension that understands its own argument shapes and risk profile.

## What Changes

- **New project `Daemon.BuiltinTools`** — references `Microsoft.Extensions.AI` and `Daemon.Protocol` only; registered into `IToolRegistry` at startup alongside user extensions.
- **Six built-in tools**: `ReadFileTool`, `WriteFileTool`, `EditFileTool`, `GlobTool`, `FetchTool`, `BashTool`. Each is an `IDaemonExtension` implementation.
- **BREAKING — `IDaemonExtension` gains two methods**:
  - `PermissionResult Evaluate(FunctionCallContent call, IPermissionSettings project, IPermissionSettings? global)` — self-evaluation replaces the category dispatch in `IPermissionPolicy`
  - `ToolConfirmRequest CreateConfirmRequest(FunctionCallContent call)` — produces the `tool.confirmRequest` payload including the correct `RiskLevel`
  - Both have default implementations so existing extensions compile without changes (defaults to `Prompt` / `Low` risk)
- **`Daemon.Extensions` gains a reference to `Daemon.Protocol`** — required for `ToolConfirmRequest` and `PermissionResult` types.
- **`IPermissionPolicy` simplified** — `EvaluateRead`, `EvaluateWrite`, `EvaluateBash`, `EvaluateHttp` are removed. The interface retains only settings access. `PermissionGateChatClient` calls `extension.Evaluate(...)` instead of dispatching by category.
- **`IToolRegistry` gains a reverse lookup** — `FindExtension(toolName)` returns the `IDaemonExtension` that registered a given tool, so the gate can call `extension.Evaluate` and `extension.CreateConfirmRequest`.
- **Attachment offloading in the pipeline** — large tool outputs (over `Daemon:Session:AttachmentThresholdBytes`, default 1 KiB) are written to `attachments/` by a new `AttachmentOffloadingChatClient` middleware inserted after `FunctionInvokingChatClient`. Tools return raw content; the pipeline handles offloading transparently.
- **`PermissionGateChatClient` stays dumb** — no tool-category logic; delegates entirely to the extension via registry lookup.

## Capabilities

### New Capabilities

- `builtin-tools`: The six built-in `IDaemonExtension` implementations and `Daemon.BuiltinTools` project structure
- `attachment-offloading`: Pipeline middleware that transparently writes large tool outputs to session attachments

### Modified Capabilities

- `extension-model`: `IDaemonExtension` gains `Evaluate` and `CreateConfirmRequest`; `Daemon.Extensions` references `Daemon.Protocol`; `IToolRegistry` gains `FindExtension`
- `permission-model`: `IPermissionPolicy.EvaluateRead/Write/Bash/Http` removed; evaluation responsibility moves to individual extensions; `PermissionGateChatClient` delegates to extensions

## Impact

- **`src/Daemon.Extensions/`** — `IDaemonExtension.cs` modified (BREAKING interface change); new `Daemon.Protocol` project reference
- **`src/Daemon.Core/`** — `PermissionGateChatClient` rewritten; `IPermissionPolicy` simplified; `IToolRegistry` + `ToolRegistry` extended; new `AttachmentOffloadingChatClient`; `DaemonServiceExtensions.AddBuiltinTools()` registration method
- **`src/Daemon.BuiltinTools/`** — new project with six tool implementations
- **`src/Daemon.Protocol/`** — `ToolConfirmRequest` type may need minor additions for `RiskLevel` on the confirm payload (already exists; verify field names)
- **`test/Daemon.Core.Tests/`** — gate tests updated; new tests for `AttachmentOffloadingChatClient`
- **`test/Daemon.BuiltinTools.Tests/`** — new test project covering each tool's `Evaluate` logic and execution behaviour
- Existing `.csx` and NuGet extensions that implement `IDaemonExtension` compile without changes (default method implementations); they will use `Prompt` / `Low` risk by default
