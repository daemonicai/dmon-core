## Context

Phases 3, 4, and 5 of the `dmon-migration` change went through reviewer audits that each flagged a small handful of cosmetic + reliability nits and architectural notes. The migration was scoped to porting `Dmon.Terminal` onto `dcli` end-to-end — any item that wasn't required for that core scope was deferred to a follow-up so the migration could stay focused and shippable.

That deferred list, captured in the archived `2026-05-28-dmon-migration/DEVLOG.md`, contains a natural cluster: five items that all touch the host's process lifecycle (`Program.cs` orchestration, `ConsoleEventHandler` event dispatch, `InputStateLayer` thread-safety, the goodbye separator on exit). Each is small enough that a separate OpenSpec change for each would be more ceremony than work. None has design ambiguity — they have clear, mechanical fixes. Bundling them into one hardening pass is the right scope.

## Goals / Non-Goals

**Goals:**

- The goodbye separator renders on Ctrl+C exit (and other graceful-shutdown paths).
- `/reload` is single-shot under rapid-fire user input; no benign double-restart window.
- `InputStateLayer.IsLocked` has well-defined cross-task visibility semantics (matching the prior `InputReader._isLocked` `volatile` contract).
- `DrainAsync` exceptions don't crash the host process silently — they log and trigger orderly shutdown.
- The `ConsoleEventHandler` adapter's two `HandleAsync` overloads are renamed so a first-time reader can immediately tell which is the RPC inbound vs UI inbound dispatch.

**Non-Goals:**

- No new UX features.
- No behaviour change to the wizard, dialog surfaces, markdown rendering, or RPC protocol.
- Not a place to address the deferred `InputStateLayer.History` decision (separate change), the picker pre-selection regression (blocked on dcli), or any MarkdownRenderer fidelity work (separate `markdown-fidelity-pass` change).
- No spec changes outside `terminal-host`.

## Decisions

### 1. Goodbye separator: move before the `await using` scope exit

`Program.cs` today (post-migration):

```csharp
await using ITerminal terminal = await Dcli.Terminal.StartAsync(...);
// ... session loop ...
await coreProcess.StopAsync();
renderer.PrintSeparator("goodbye");  // ← runs after dcli teardown if scope exits first
```

Fix: move `renderer.PrintSeparator("goodbye")` so it runs **inside** the `await using` block, *before* `coreProcess.StopAsync()`. The user sees the separator while the dcli fixed region is still alive; the core stops; the dcli scope tears down on exit.

Rejected: catching the dispose error and re-creating a renderer to print the separator post-teardown — fragile, more complex than reorder.

### 2. `reloadSignal` recreation: after `RestartAsync` returns

Phase 4's introduced flow in `RunSessionAsync`:

```csharp
reloadSignal = new TaskCompletionSource<bool>();  // ← (A) before RestartAsync
await coreProcess.RestartAsync();
// ← (B) safe point to recreate
```

A second `/reload` arriving between (A) and the next session-loop iteration lights up the already-new signal and triggers a second restart. Move signal recreation to (B). The reviewer's note flagged this as benign-but-noisy; the fix is one line.

### 3. `InputStateLayer.IsLocked` is `volatile bool`

The previous `InputReader._isLocked` was declared `volatile` to bridge a polling thread (reader) and the RPC dispatch thread (writer). The current `InputStateLayer.IsLocked` field is read in `HandleUiEventAsync` (running on `DrainAsync`'s task) and written in `HandleRpcEventAsync` (running on the session-loop task). Both are async, no formal same-thread guarantee; the CLR memory model permits reordering of plain `bool` reads/writes across task boundaries.

Fix: change the backing field to `volatile bool`. The property accessor is unchanged. This is the cheapest correct fix; alternatives (`Interlocked.Exchange`, `lock` blocks) are over-engineered for a single-flag flip.

### 4. `DrainAsync` exception handling: log + cancel + exit cleanly

Today:

```csharp
public async Task DrainAsync(ChannelReader<TerminalEvent> reader, CancellationToken cancellationToken)
{
    try
    {
        await foreach (TerminalEvent ev in reader.ReadAllAsync(cancellationToken))
            await HandleUiEventAsync(ev, cancellationToken);
    }
    catch (OperationCanceledException) { }
}
```

If dcli ever calls `Writer.Complete(exception)` (which it doesn't today, but the channel API allows it), the fault propagates out of `DrainAsync`, faults `Task.WhenAll(drainTask, ...)` in `Program.cs`, and surfaces as an unhandled exception with no log. The fix:

```csharp
try
{
    await foreach (TerminalEvent ev in reader.ReadAllAsync(cancellationToken))
        await HandleUiEventAsync(ev, cancellationToken);
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    _renderer.AddSystemLine($"[Drain Error] {ex.GetType().Name}: {ex.Message}");
    _cts.Cancel();
}
```

The host logs the fault to the scrollback (so the user sees what happened), cancels the CTS (so the session loop tears down cleanly), and `DrainAsync` returns normally. `Task.WhenAll` doesn't fault. Exit is orderly with a visible diagnostic.

Rejected:
- Rethrow + handle in `Program.cs`: still ends in `Task.WhenAll` fault, just moves the catch site.
- Log + continue (try to drain past the fault): if dcli's channel is broken, draining isn't going to recover. Cancel is the right exit.

### 5. `HandleAsync` rename: `HandleRpcEventAsync` / `HandleUiEventAsync`

The two overloads:

```csharp
public Task HandleAsync(Event @event, CancellationToken cancellationToken);          // RPC inbound (core → host)
public Task HandleAsync(TerminalEvent @event, CancellationToken cancellationToken);  // UI inbound (dcli → host)
```

Rename:

```csharp
public Task HandleRpcEventAsync(Event @event, CancellationToken cancellationToken);
public Task HandleUiEventAsync(TerminalEvent @event, CancellationToken cancellationToken);
```

Update the three call sites:
- `Program.cs` session loop: `dispatcher.Events.TryRead(out evt)` → `handler.HandleRpcEventAsync(evt, ...)`.
- `Program.cs` drainTask setup: `handler.DrainAsync(terminal.Events, ...)` (internal) calls `HandleUiEventAsync` per event.
- `ConsoleEventHandlerTests.cs` — every test currently calling `HandleAsync` adjusts to the appropriate new name.

This is purely a readability win. No behaviour change.

## Risks / Trade-offs

- **Risk: the `DrainAsync` exception path is currently untested in production** because dcli never signals channel faults. The new path is exercised in tests but the production trigger is dormant. *Mitigation:* the new tier-A test (`DrainAsync_NonCancelException_LogsAndCancels`) drives a synthetic exception through a hand-rolled `Channel<TerminalEvent>` and asserts the log + cancel behaviour. When dcli eventually does fault a channel (or some other library does), the path is ready.
- **Risk: declaring `IsLocked` as `volatile` doesn't fix every memory-model concern** — only the visibility of the single flag. Operations that need atomicity across multiple fields (e.g., "check IsLocked AND update History atomically") would still need a lock. *Mitigation:* the only correctness concern flagged by the reviewer was the single-flag visibility; no caller needs cross-field atomicity. If that changes, the fix evolves with the use case.
- **Risk: the goodbye separator reorder may interact with the `Console.CancelKeyPress` redundancy-net path** — that path also calls `_cts.Cancel()` and the session loop may exit before reaching the goodbye separator if the cancellation token observes first. *Mitigation:* test both shutdown paths (dcli `KeyPressed(Ctrl+C)` and OS-level `Console.CancelKeyPress`). If one bypasses the separator, decide whether to special-case or accept that the OS-level path is the "fast exit, no goodbye" path.
- **Risk: the `HandleAsync` rename touches every test in `ConsoleEventHandlerTests`** (~25 methods). *Mitigation:* mechanical find-and-replace; build will fail fast if any call site is missed. The fake `OnSelectAsync` / `OnChoiceAsync` etc. in `FakeTerminal` are unaffected — only the production type's method names change.

## Migration Plan

This change has no version-coupling, no upstream dependencies, and no migration phasing. Land in a single OpenSpec section per item (5 sections in `tasks.md`) or bundled into 2-3 sections, then gate + reviewer + commit per section per the standard `CLAUDE.md` apply workflow. Branch is `change/terminal-host-hardening` off `main`.

Rollback: each item is independently revertable. If one item turns out wrong (e.g., the `volatile` declaration introduces an unexpected drift), revert just that one and proceed.

## Open Questions

1. Should the `DrainAsync` exception path also `await coreProcess.StopAsync()` before cancelling, or rely on the outer session loop's cleanup to handle the core process? *Tentative answer:* rely on the outer cleanup. `DrainAsync` should be focused on its own responsibility (event drain); the orchestrator (`Program.cs`) owns process lifecycle.
2. The goodbye separator — should it also render on non-Ctrl+C exit paths (e.g., when the core process exits unexpectedly and the session loop returns false)? *Tentative answer:* yes, but those paths already pass through the same shutdown code. If the reorder lands inside the `await using` scope, all graceful-exit paths get the separator.
