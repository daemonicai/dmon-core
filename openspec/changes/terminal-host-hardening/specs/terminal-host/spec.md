## ADDED Requirements

### Requirement: Host lifecycle hardening

The terminal host SHALL be defensive about three lifecycle concerns flagged during the `dmon-migration` reviewer audits: (1) `/reload` SHALL be single-shot under rapid-fire user input — the session loop SHALL recreate its restart-signal `TaskCompletionSource` *after* `CoreProcessManager.RestartAsync()` returns, not before, so a second `/reload` arriving during the restart window cannot trigger a benign double-restart; (2) `InputStateLayer.IsLocked` SHALL be cross-task visible — its backing field SHALL be declared `volatile bool` (or the equivalent memory-barrier guard) so a write on the RPC-dispatch task is observable to a subsequent read on the UI-dispatch task without reordering; (3) `ConsoleEventHandler.DrainAsync` SHALL handle non-cancellation exceptions gracefully — non-`OperationCanceledException` exceptions thrown out of `ChannelReader.ReadAllAsync` or the per-event dispatch SHALL be logged to the scrollback (one `[Drain Error] {type}: {message}` line) and SHALL trigger `CancellationTokenSource.Cancel()` on the host's outer CTS, so the session loop tears down cleanly rather than faulting `Task.WhenAll`.

#### Scenario: Rapid /reload single-shot

- **WHEN** the user submits `/reload` once via `dcli`'s `InputSubmitted` event and then submits `/reload` again while `CoreProcessManager.RestartAsync()` is still in flight
- **THEN** the host restarts the core process exactly once (not twice) and emits exactly one `[Reload] Core restarted.` system line

#### Scenario: IsLocked cross-task visibility

- **WHEN** `HandleRpcEventAsync` writes `_input.IsLocked = true` on `TurnStartEvent` (session-loop task) and `HandleUiEventAsync` reads `_input.IsLocked` on a subsequent `InputSubmitted` event (DrainAsync task)
- **THEN** the read on the UI task observes the write from the RPC task (no reordering or staleness across the task boundary)

#### Scenario: DrainAsync non-cancellation exception logged and cancelled

- **WHEN** `ConsoleEventHandler.DrainAsync` observes a non-`OperationCanceledException` exception from the channel reader or per-event dispatch
- **THEN** the host appends one `[Drain Error] {type}: {message}` line to the scrollback, calls `Cancel()` on the host's `CancellationTokenSource`, returns from `DrainAsync` normally (does not rethrow), and the outer session loop's `Task.WhenAll` completes successfully

## MODIFIED Requirements

### Requirement: Ctrl+C exits cleanly

The terminal host SHALL handle Ctrl+C via `dcli`'s `Events.KeyPressed` event (which surfaces `KeyEvent(Char('c'), Modifiers.Ctrl)` per `dcli`'s key-encoding contract — `dcli` does not terminate the process for the consumer) and SHALL perform a graceful shutdown: render the goodbye separator to scrollback *before* dcli's fixed region tears down, stop the core process, cancel background tasks, exit with code 0.

#### Scenario: Ctrl+C during idle

- **WHEN** the user presses Ctrl+C while no turn is active and `dcli` emits `KeyPressed(Char('c'), Ctrl)`
- **THEN** the host stops the core process and exits

#### Scenario: Ctrl+C during streaming

- **WHEN** the user presses Ctrl+C while a turn is streaming and `dcli` emits `KeyPressed(Char('c'), Ctrl)`
- **THEN** the host cancels the active turn's `CancellationToken`, stops the core process, and exits

#### Scenario: Goodbye separator rendered before dcli teardown

- **WHEN** the host enters the graceful-shutdown path (Ctrl+C, the legacy `Console.CancelKeyPress` redundancy net, or the core process exiting unexpectedly)
- **THEN** `renderer.PrintSeparator("goodbye")` runs while dcli's `ITerminal` is still alive (i.e. before the `await using ITerminal terminal` scope exits), so the user sees the goodbye separator in their terminal scrollback after the app exits
