## 1. Goodbye separator + `/reload` timing in `Program.cs`

- [x] 1.1 Move `renderer.PrintSeparator("goodbye")` from after `coreProcess.StopAsync()` to before it (and before the `await using ITerminal terminal` scope exits). The user sees the separator while the dcli fixed region is still alive.
- [x] 1.2 Move the `reloadSignal = new TaskCompletionSource<bool>()` recreation to *after* `await coreProcess.RestartAsync()` returns, not before. Eliminates the rapid-fire double-restart window.
- [x] 1.3 Tier-A test: add a `Program`-level integration check via a small refactor to expose the goodbye-render call point, OR cover with a tier-B `HeadlessTerminal` test that drives a Ctrl+C and asserts the goodbye separator is in the final scrollback frame. **Recommendation:** tier-B harness — `Program.cs` orchestration is hard to tier-A test cleanly.
- [x] 1.4 Tier-A test: `RunSessionAsync_RapidReload_OnlyRestartsOnce` — script two `_requestReload()` calls in quick succession via the existing `FakeTerminal.PublishEventAsync` of two `/reload` InputSubmitted events; assert `CoreProcessManager.RestartAsync` is called exactly once. (May need to inject a fake `CoreProcessManager` or assert via the underlying `Program` flow — judgement call for the worker.)
- [x] 1.5 Standard gates: `dotnet build`, `dotnet test`, `openspec validate terminal-host-hardening --strict`; reviewer audit; commit.

## 2. `InputStateLayer.IsLocked` is `volatile bool`

- [x] 2.1 Refactor the backing field in `src/Dmon.Terminal/InputStateLayer.cs` from `private bool _isLocked` to `private volatile bool _isLocked` (or equivalent — if `IsLocked` is currently an auto-property, convert to a backing field + explicit get/set so the field can carry the `volatile` modifier).
- [x] 2.2 Confirm via comment that this matches the previous `InputReader._isLocked` contract — a one-line WHY-comment is enough since the rationale is non-obvious from the code.
- [x] 2.3 No new tests needed (volatility is unobservable from xUnit; existing `InputStateLayerTests` and `ConsoleEventHandlerTests` cover the lock behaviour at the API level). Document in the commit message that this is a memory-model correctness fix.
- [x] 2.4 Standard gates + reviewer + commit.

## 3. `DrainAsync` exception handling

- [x] 3.1 In `src/Dmon.Terminal/ConsoleEventHandler.cs`, extend the `try` block in `DrainAsync` to catch non-cancellation exceptions: log via `_renderer.AddSystemLine($"[Drain Error] {ex.GetType().Name}: {ex.Message}")`, call `_cts.Cancel()`, return normally (do not rethrow). Keep the existing `catch (OperationCanceledException) { }` arm.
- [x] 3.2 Tier-A test in `ConsoleEventHandlerTests.cs`: `DrainAsync_NonCancelException_LogsAndCancels`. Construct a `Channel<TerminalEvent>`, write one event, then complete the writer with `Writer.Complete(new InvalidOperationException("synthetic"))`. Assert `_renderer`'s call log contains an `AddSystemLine` with "[Drain Error]" prefix and that the supplied `cts.IsCancellationRequested` is `true` after `DrainAsync` returns.
- [x] 3.3 Standard gates + reviewer + commit.

## 4. `HandleAsync` overload rename

- [ ] 4.1 In `src/Dmon.Terminal/ConsoleEventHandler.cs`, rename `HandleAsync(Event @event, CancellationToken cancellationToken)` → `HandleRpcEventAsync` and `HandleAsync(TerminalEvent @event, CancellationToken cancellationToken)` → `HandleUiEventAsync`. Update the internal `DrainAsync` body to call `HandleUiEventAsync`.
- [ ] 4.2 In `src/Dmon.Terminal/Program.cs`, update the session loop's RPC dispatch call site: `handler.HandleAsync(evt, ...)` → `handler.HandleRpcEventAsync(evt, ...)`.
- [ ] 4.3 In `test/Dmon.Terminal.Tests/ConsoleEventHandlerTests.cs`, update every call to `handler.HandleAsync(...)` to the appropriate new name (each test currently dispatches either an `Event` subtype or a `TerminalEvent` subtype — the right rename is unambiguous per call site). Estimated ~25 method renames; all mechanical.
- [ ] 4.4 Standard gates + reviewer + commit.

## 5. Manual smoke + archive

- [ ] 5.1 Manual smoke: `make build && build/dmon` then exercise the no-LLM recipe from the `dmon-migration` DEVLOG (typing, `/reload` end-to-end, Ctrl+C). Confirm: (a) `── goodbye ──` renders on Ctrl+C exit, (b) mashing `/reload` twice in quick succession produces only one `[Reload] Core restarted.` not two. (This step unblocks only after both the `Dmon.Core` MCP/M.E.AI crash is resolved AND the user has API keys configured; until then, the gates above are the verification.)
- [ ] 5.2 Standard gates + reviewer + commit (gates were already run per-section; this section runs the final sweep).
- [ ] 5.3 Propose `/opsx:archive terminal-host-hardening` and wait for user confirmation. Do not archive automatically.
