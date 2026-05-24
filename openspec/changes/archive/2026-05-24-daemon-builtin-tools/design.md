## Context

daemon's infrastructure (turn loop, permission gate, tool registry, session storage) is complete but the agent has no tools to offer the LLM. The original design left tool implementations as a follow-on concern. Introducing them now also exposes a design issue: `IPermissionPolicy` has four methods (`EvaluateRead`, `EvaluateWrite`, `EvaluateBash`, `EvaluateHttp`) that encode tool-category knowledge in the Core layer — but that knowledge only exists because we anticipated what tools we'd build. The evaluation logic belongs closer to the tools themselves.

Current state:
- `IPermissionPolicy.EvaluateBash` internally calls `IBashCompositeDetector` and `IDenylistChecker`
- `PermissionGateChatClient.EvaluateToolCall` is a stub that returns `Prompt` for everything
- `IDaemonExtension` exposes only `GetTools()`; extensions have no voice in their own permission evaluation

## Goals / Non-Goals

**Goals:**
- Six built-in tools (Read, Write, Edit, Glob, Fetch, Bash) available to the LLM on startup
- Self-evaluating extension contract: extensions declare their own permission logic
- Gate stays dumb — no tool-category dispatch in Core
- Large tool outputs transparently offloaded to session attachments by the pipeline
- Existing third-party extensions continue to compile (default interface implementations)

**Non-Goals:**
- Tool output streaming to the LLM (tool results are collected whole)
- Sandboxed execution environments for bash
- Windows-native bash alternatives (`cmd.exe`, PowerShell); `Process.Start` uses the system shell
- Per-tool configurable timeouts for V1

## Decisions

### D1: `Daemon.BuiltinTools` is a separate project, not a folder in `Daemon.Core`

Built-in tools need only `Microsoft.Extensions.AI` and `Daemon.Protocol` — no Core internals. A separate project makes this dependency boundary explicit and keeps `Daemon.Core` from accreting tool implementation code. `Daemon.Core` references `Daemon.BuiltinTools` for startup registration; `Daemon.BuiltinTools` does not reference `Daemon.Core`. No circular dependency.

**Alternative considered**: `src/Daemon.Core/Tools/` folder. Rejected: blurs the boundary between infrastructure and tool behaviour; harder to test in isolation.

### D2: `IDaemonExtension` gains `Evaluate` and `CreateConfirmRequest` with default implementations

```csharp
public interface IDaemonExtension
{
    string Name { get; }
    string Description { get; }
    IEnumerable<AIFunction> GetTools();

    // Default: Prompt for everything. Override to implement fine-grained policy.
    PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
        => PermissionResult.Prompt;

    // Default: Low risk, name+args from call. Override for accurate risk level.
    ToolConfirmRequest CreateConfirmRequest(FunctionCallContent call)
        => new() { Id = call.CallId, Name = call.Name, Args = call.Arguments ?? [], Risk = RiskLevel.Low };
}
```

Default implementations mean existing extensions remain valid without recompilation. The gate calls `extension.Evaluate(call, projectSettings, globalSettings)` and `extension.CreateConfirmRequest(call)`.

**Alternative considered**: Separate `IPermissionAwareExtension` opt-in interface. Rejected: forces double-dispatch in the gate; default methods on the existing interface are cleaner and keep the contract in one place.

### D3: `IPermissionPolicy` retains settings access, loses category methods

`IPermissionPolicy.EvaluateRead/Write/Bash/Http` are removed. `IPermissionPolicy` becomes a thin wrapper that provides `IPermissionSettings` for both project and global scopes to callers that need them (i.e., extension `Evaluate` implementations). `PermissionGateChatClient` is injected with `IPermissionPolicy` only to obtain the settings; all evaluation logic lives in extensions.

`BashTool.Evaluate` inlines the composite detection and denylist logic (previously in `PermissionPolicy.EvaluateBash`). The `IBashCompositeDetector` and `IDenylistChecker` implementations move to `Daemon.BuiltinTools`; their interfaces remain in `Daemon.Core` (still needed by `PermissionPolicy` for the settings-access path — or they can move too if `PermissionPolicy` is further simplified).

**Trade-off**: A malicious extension can override `Evaluate` to return `Allow`. This is accepted for V1 — extensions are user-loaded, trusted code. Revisit at V1.5 when extension provenance and signing may be added. The hardcoded denylist in `BashTool` is not bypassable by other extensions because it's baked into `BashTool.Evaluate`; a custom bash-running extension that doesn't use `BashTool` is responsible for its own safety.

### D4: `IToolRegistry` gains `FindExtension(toolName)`

```csharp
IDaemonExtension? FindExtension(string toolName);
```

`ToolRegistry` maintains a `Dictionary<string, IDaemonExtension>` alongside the existing tool list, populated when `Register(extensionName, extension, tools)` is called. The gate calls `FindExtension(call.Name)` to locate the owning extension.

`Register` signature changes: `Register(string extensionName, IDaemonExtension extension, IEnumerable<AIFunction> tools)` — the extension instance is now stored, not just the tool list.

### D5: Attachment offloading via `AttachmentOffloadingChatClient` middleware

A new `IChatClient` middleware inserted after `FunctionInvokingChatClient` inspects every `FunctionResultContent` in the response. If the result string exceeds `Daemon:Session:AttachmentThresholdBytes` (default 1 KiB), it writes the content to `attachments/<callId>.txt` and replaces the result with a compact JSON object:

```json
{"attachmentPath": "attachments/<callId>.txt", "preview": "<first 200 chars>..."}
```

Tools return raw strings. The middleware is the only place that knows about `IAttachmentStore`. Pipeline order:

```
PermissionGateChatClient
  → FunctionInvokingChatClient
  → AttachmentOffloadingChatClient   ← new
  → RetryingChatClient
  → provider client
```

**Alternative considered**: Tools call `IAttachmentStore` directly. Rejected: cross-cutting concern; would require every tool to take an `IAttachmentStore` dependency and know the current session id.

### D6: Built-in tool argument conventions

Each tool uses a consistent argument naming convention so `Evaluate` implementations can extract the relevant argument reliably:

| Tool | Key argument | Permission category |
|------|-------------|---------------------|
| `read_file` | `path` | Read |
| `write_file` | `path` | Write |
| `edit_file` | `path` | Write |
| `glob` | `pattern` | Read (CWD implicit) |
| `fetch` | `url` | HTTP (domain extracted) |
| `bash` | `command` | Bash |

Tool names use snake_case to match LLM tool-calling conventions. The `Name` property of each `AIFunction` matches this table.

### D7: `BashTool` uses `Process.Start` with the system shell

Bash commands are executed via `Process.Start("/bin/sh", ["-c", command])` on POSIX and `Process.Start("cmd.exe", ["/c", command])` on Windows. stdout and stderr are both captured; combined output is returned as the tool result. Process timeout defaults to 30 seconds, configurable via `Daemon:Tools:Bash:TimeoutSeconds`.

## Risks / Trade-offs

- **Extension self-evaluation is trusted** → Mitigated by V1 scope (user explicitly loads extensions); revisit for V1.5 signed extensions.
- **BashTool denylist duplication** → The list previously lived in `DenylistChecker`; it now moves to `BashTool`. The original tests move with it. One source of truth, different location.
- **`Register` signature is breaking for `IToolRegistry`** → Only `ExtensionService` and `BuiltinToolsService` call `Register`; internal callers only. Test updates required.
- **Attachment middleware requires active session** → `AttachmentOffloadingChatClient` needs the current session id. It receives this from `SessionHandler.CurrentSession`. If no session is active, offloading is skipped and the full result is inlined.
- **Large bash output on context window** → Mitigated by attachment offloading. 1 KiB default is conservative; most command outputs that matter fit within it.

## Open Questions

*(none — resolved in explore session)*
