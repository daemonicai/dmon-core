## Why

Five small reliability and readability items were flagged by reviewer audits across Phases 3, 4, and 5 of the `dmon-migration` change, then deliberately deferred so the migration could stay focused. Now that the migration is shipped, address them as one bundled cleanup pass.

Each item is narrow, mechanical, and independently rollback-able. They share a single OpenSpec change because they all touch the terminal-host process lifecycle (`Program.cs`, `ConsoleEventHandler.cs`, `InputStateLayer.cs`) in the same review pass, and bundling avoids five micro-proposals with five separate review cycles for items that have no design ambiguity.

The five items, with their origins in the archived `2026-05-28-dmon-migration` DEVLOG:

1. **Goodbye separator rendering** — `Program.cs` calls `renderer.PrintSeparator("goodbye")` *after* `coreProcess.StopAsync()`, but the `await using ITerminal terminal` scope tears down dcli's fixed region *before* that line runs, so the goodbye separator never reaches a live fixed region. Cosmetic but visible.
2. **`/reload` rapid-fire double-restart window** — Phase 4 introduced a `TaskCompletionSource<bool> reloadSignal` to unblock the session loop when input flows through `DrainAsync`. The signal is re-created at the top of each `RunSessionAsync` iteration *before* `coreProcess.RestartAsync()` returns. A user who mashes `/reload` during the restart window can land a second `TrySetResult(true)` on the already-new signal, causing a benign-but-noisy second restart.
3. **`InputStateLayer.IsLocked` not declared `volatile`** — the previous `InputReader._isLocked` was `volatile bool` to bridge the polling thread and the dispatch thread. The new model has both touch points on async tasks with no formal same-thread guarantee; the CLR memory model permits reordering. Worst case is one extra submission forwarded/dropped across a `TurnStart` / `TurnEnd` boundary.
4. **`DrainAsync` only swallows `OperationCanceledException`** — other exceptions from `ReadAllAsync` (e.g. if dcli ever signals a channel fault via `Writer.Complete(exception)`) propagate out of `DrainAsync`, fault the host's outer `Task.WhenAll`, and surface as an unhandled exception. Dormant today (dcli doesn't fault channels), but the host should fail loudly and shut down cleanly rather than crashing silently.
5. **`HandleAsync` overload pair confuses readers** — `ConsoleEventHandler` exposes two `HandleAsync` methods, one taking `Event` (RPC inbound from core) and one taking `TerminalEvent` (UI inbound from dcli). C# overload resolution disambiguates cleanly, but a reader new to the file has to look twice. Rename to `HandleRpcEventAsync` / `HandleUiEventAsync`.

## What Changes

- **`src/Dmon.Terminal/Program.cs`** — move `renderer.PrintSeparator("goodbye")` so it runs before the `await using ITerminal terminal` scope exits (and before `coreProcess.StopAsync()`); recreate `reloadSignal` *after* `coreProcess.RestartAsync()` returns, not before.
- **`src/Dmon.Terminal/InputStateLayer.cs`** — declare the backing field for `IsLocked` as `volatile bool`. The public property stays, but it now reads/writes a volatile field; matches the old `InputReader._isLocked` contract.
- **`src/Dmon.Terminal/ConsoleEventHandler.cs`** — wrap the `await foreach` in `DrainAsync` so non-cancellation exceptions are caught, surfaced via `_renderer.AddSystemLine($"[Drain Error] ...")`, and trigger `_cts.Cancel()` for orderly shutdown rather than propagating into `Task.WhenAll`. Rename `HandleAsync(Event)` → `HandleRpcEventAsync(Event)` and `HandleAsync(TerminalEvent)` → `HandleUiEventAsync(TerminalEvent)`. Update the production call sites in `Program.cs` (`dispatcher.Events.TryRead` → `HandleRpcEventAsync`; `DrainAsync` internal dispatch → `HandleUiEventAsync`).
- **Tests** — update `ConsoleEventHandlerTests` to call the new method names; add a `DrainAsync_NonCancelException_LogsAndCancels` test for the new exception path; add a `Program`-level note or `InputStateLayerTests` assertion for `IsLocked` (no observable change, but the rename of the field touches the file).
- **No behavioural change visible to the user** beyond (a) `── goodbye ──` actually appearing on Ctrl+C exit and (b) `/reload` no longer producing a double-restart message when mashed during the restart window.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `terminal-host` — adds two scenarios under "Ctrl+C exits cleanly" (goodbye separator visible) and one new requirement "Host lifecycle hardening" covering /reload single-shot, IsLocked visibility, and DrainAsync exception handling.
