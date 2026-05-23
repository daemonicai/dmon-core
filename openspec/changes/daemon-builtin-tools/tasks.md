## 1. Foundation — Project Setup

- [x] 1.1 Create `src/Daemon.BuiltinTools/Daemon.BuiltinTools.csproj` referencing `Microsoft.Extensions.AI` and `Daemon.Extensions` (which transitively provides `Daemon.Protocol`); add to `Daemon.slnx`
- [x] 1.2 Create `test/Daemon.BuiltinTools.Tests/Daemon.BuiltinTools.Tests.csproj` referencing `Daemon.BuiltinTools`, `xunit`, and `Microsoft.NET.Test.Sdk`; add to `Daemon.slnx`
- [x] 1.3 Add a project reference from `Daemon.Core` to `Daemon.BuiltinTools` (Core registers built-in tools at startup)
- [x] 1.4 Add a project reference from `Daemon.Extensions` to `Daemon.Protocol` (needed for `PermissionResult`, `IPermissionSettings`, `ToolConfirmRequest` on the updated `IDaemonExtension`)

## 2. Extension Model — Interface Changes

- [x] 2.1 Add `PermissionResult Evaluate(FunctionCallContent call, IPermissionSettings project, IPermissionSettings? global)` to `IDaemonExtension` with default implementation returning `PermissionResult.Prompt`
- [x] 2.2 Add `ToolConfirmRequest CreateConfirmRequest(FunctionCallContent call)` to `IDaemonExtension` with default implementation returning `new ToolConfirmRequest { Id = call.CallId, Name = call.Name, Args = call.Arguments ?? [], Risk = RiskLevel.Low }`
- [x] 2.3 Change `IToolRegistry.Register` signature to `Register(string extensionName, IDaemonExtension extension, IEnumerable<AIFunction> tools)` — breaking internal change
- [x] 2.4 Add `IDaemonExtension? FindExtension(string toolName)` to `IToolRegistry`
- [x] 2.5 Update `ToolRegistry` to maintain a `Dictionary<string, IDaemonExtension>` (keyed by tool name, populated during `Register`) and implement `FindExtension`; update `Register` to accept the `IDaemonExtension` parameter
- [x] 2.6 Update all existing callers of `IToolRegistry.Register` (`ExtensionService`, `BuiltinToolsService` stubs if any) to pass the `IDaemonExtension` instance

## 3. Permission Model — Simplification

- [x] 3.1 Remove `EvaluateRead`, `EvaluateWrite`, `EvaluateBash`, and `EvaluateHttp` from `IPermissionPolicy`; add `IPermissionSettings ProjectSettings { get; }` and `IPermissionSettings? GlobalSettings { get; }` properties
- [x] 3.2 Rewrite `PermissionPolicy` to implement the simplified `IPermissionPolicy` (expose settings; remove category evaluation methods; retain path-matching and glob-matching helpers as `internal static` methods on the class for reuse by built-in tools)
- [x] 3.3 Update `PermissionGateChatClient.EvaluateToolCall` to call `IToolRegistry.FindExtension(call.Name)?.Evaluate(call, policy.ProjectSettings, policy.GlobalSettings) ?? PermissionResult.Prompt` and `extension.CreateConfirmRequest(call)` for the confirm payload — remove all category-dispatch logic
- [x] 3.4 Update `DaemonServiceExtensions.AddDaemonCore()` registration so `IPermissionPolicy` is registered with the simplified implementation (remove any `IBashCompositeDetector`/`IDenylistChecker` arguments from the `PermissionPolicy` constructor now that those are moved to `BashTool`)
- [x] 3.5 Move `IBashCompositeDetector` and `IDenylistChecker` interfaces and their implementations into `Daemon.BuiltinTools` (remove from `Daemon.Core` if no other Core component still references them; if Core still holds the interfaces, move only the implementations)

## 4. Built-in Tools — Implementations

- [x] 4.1 Implement `ReadFileTool : IDaemonExtension` — `AIFunction` name `read_file`, argument `path`; reads file text; returns error string on failure; `Evaluate` returns `Allow` for paths under CWD, `Prompt` otherwise
- [x] 4.2 Implement `WriteFileTool : IDaemonExtension` — `AIFunction` name `write_file`, arguments `path` + `content`; creates/overwrites file (with parent dirs); `Evaluate` returns `Prompt` always (writes always need confirmation); `CreateConfirmRequest` returns `RiskLevel.High`
- [x] 4.3 Implement `EditFileTool : IDaemonExtension` — `AIFunction` name `edit_file`, arguments `path` + `old_string` + `new_string`; replaces first occurrence; returns error string if `old_string` not found or file missing; `Evaluate` returns `Prompt` always; `CreateConfirmRequest` returns `RiskLevel.High`
- [x] 4.4 Implement `GlobTool : IDaemonExtension` — `AIFunction` name `glob`, argument `pattern`; returns newline-separated matching paths relative to CWD; empty result is a valid empty string; `Evaluate` returns `Allow`
- [x] 4.5 Implement `FetchTool : IDaemonExtension` — `AIFunction` name `fetch`, argument `url`; HTTP GET via `HttpClient`; returns body on 2xx; returns error string with status code on 4xx/5xx; returns error string on network failure; `Evaluate` extracts domain from `url` and checks against `policy.ProjectSettings.Settings.Http.Allow`; `CreateConfirmRequest` returns `RiskLevel.Medium`
- [x] 4.6 Implement `BashTool : IDaemonExtension` — `AIFunction` name `bash`, argument `command`; runs via `/bin/sh -c` on POSIX / `cmd.exe /c` on Windows; captures stdout+stderr combined; returns `"Exit <code>: <output>"` on non-zero; kills process and returns `"Error: timed out"` after `Daemon:Tools:Bash:TimeoutSeconds` (default 30); `Evaluate` runs denylist check first (returns `Deny`), then composite detection (returns `Prompt`), then checks project/global bash allow/deny patterns; `CreateConfirmRequest` returns `RiskLevel.High`

## 5. Attachment Offloading Middleware

- [x] 5.1 Implement `AttachmentOffloadingChatClient : IChatClient` in `Daemon.Core` — inspects every `FunctionResultContent` in streaming updates from the inner client; if the result string exceeds `Daemon:Session:AttachmentThresholdBytes` (default 1024) and a session is active, writes to `attachments/<callId>.txt` and replaces the result with `{"attachmentPath":"attachments/<callId>.txt","preview":"<first 200 chars>..."}`
- [x] 5.2 Inject `ISessionHandler` (for `CurrentSession`) and `IAttachmentStore` into `AttachmentOffloadingChatClient`; skip offloading silently when `CurrentSession` is null
- [x] 5.3 Update `TurnHandler.RunTurnAsync` pipeline assembly to insert `AttachmentOffloadingChatClient` between `FunctionInvokingChatClient` and `RetryingChatClient`

## 6. Registration and Wiring

- [ ] 6.1 Create `BuiltinToolsExtensions.cs` in `Daemon.BuiltinTools` with a static `AddBuiltinTools(this IToolRegistry registry)` method that registers all six built-in tool extensions (one `IDaemonExtension` instance per tool)
- [ ] 6.2 Call `AddBuiltinTools()` from `DaemonServiceExtensions.AddDaemonCore()` (or from `BootstrapService`) so built-in tools are registered before the first turn
- [ ] 6.3 Register `AttachmentOffloadingChatClient` (or its dependencies) in DI so `TurnHandler` can resolve it; confirm `IAttachmentStore` is already registered (it should be from group 9)
- [ ] 6.4 Register `FetchTool`'s `HttpClient` dependency via `IHttpClientFactory` or a singleton `HttpClient` in `AddBuiltinTools` / DI; `FetchTool` must not construct its own `HttpClient` per-call

## 7. Tests

- [ ] 7.1 Write unit tests for `ReadFileTool.Evaluate` — Allow inside CWD, Prompt outside CWD
- [ ] 7.2 Write unit tests for `WriteFileTool` execution — successful write creates/overwrites file; write to invalid path returns error string
- [ ] 7.3 Write unit tests for `EditFileTool` execution — successful replacement; `old_string` not found returns error string
- [ ] 7.4 Write unit tests for `GlobTool` execution — pattern with matches; pattern with no matches returns empty string
- [ ] 7.5 Write unit tests for `BashTool.Evaluate` — denylist command returns `Deny`; composite command returns `Prompt`; allowed command returns `Allow`
- [ ] 7.6 Write unit tests for `BashTool` execution — command exits 0 returns output; command exits non-zero returns prefixed error; command times out returns `"Error: timed out"` (use a very short timeout in tests)
- [ ] 7.7 Write unit tests for `AttachmentOffloadingChatClient` — result below threshold passes through unchanged; result above threshold is replaced with JSON object; no active session passes through unchanged
- [ ] 7.8 Update `Daemon.Core.Tests` gate tests to reflect the simplified `IPermissionPolicy` (no category methods); add tests verifying the gate calls `FindExtension` and delegates to `extension.Evaluate`
- [ ] 7.9 Verify build: `dotnet build` succeeds with zero warnings; `dotnet test` passes all tests
