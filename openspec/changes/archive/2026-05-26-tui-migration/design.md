## Context

`Dmon.Console` is the console host for dmon. It spawns the agent core process, reads JSONL events from stdio via `EventDispatcher`, renders them, and handles user input. It currently uses Spectre.Console for all rendering and input.

The fundamental bug: `MainLoopAsync` drains the event channel with `TryRead` (non-blocking), then immediately falls through to `AnsiConsole.Prompt`. That call blocks stdin. While the user waits for a response, the core is emitting `messageDelta` events into the channel — but they can never be rendered because `AnsiConsole.Prompt` owns the terminal. The response is only visible if the user types something else, triggering the next loop iteration.

This is not a fixable race condition — it is a structural incompatibility between Spectre.Console's synchronous blocking prompt model and an async streaming agent.

## Goals / Non-Goals

**Goals:**
- Replace Spectre.Console with Terminal.Gui v2
- Display agent responses as they stream, in real time
- Disable user input while a turn is active; re-enable on completion
- Render markdown (code blocks, inline code, bullets, bold/italic) on turn completion
- Preserve all existing slash command, tool confirm, UI input, session, and provider behaviours
- Redesign the setup wizard as a composable step-dialog sequence

**Non-Goals:**
- Syntax highlighting within code blocks (V1 scope)
- Themes or colour scheme customisation
- Mouse support beyond what Terminal.Gui provides by default
- Any changes to the agent core, protocol, or session storage

## Decisions

### Decision: Terminal.Gui v2 over alternatives

**Chosen:** Terminal.Gui v2 (`Terminal.Gui` NuGet package, v2.x)

**Alternatives considered:**
- *Stay with Spectre.Console, use a dedicated input thread*: Requires `Console.ReadLine` on a background thread and careful synchronisation to interleave output. Fragile, fights the library, doesn't give us the layout model we need.
- *Raw ANSI escape codes*: Total control, but reimplements layout, focus, and input handling from scratch. Disproportionate to the problem.
- *gui.cs v1*: Predecessor to Terminal.Gui v2. Still works but v2 is the maintained line and has a cleaner API.

**Rationale:** Terminal.Gui v2 has a proper event loop that separates rendering from input. UI updates from background tasks are marshalled via `Application.MainLoop.Invoke`. It provides `TextView`, `TextField`, `Dialog`, and layout primitives that map directly onto the dmon UI shape. It is cross-platform and MIT-licensed.

---

### Decision: TurnBlock model for output rendering

**Chosen:** `ChatOutputView` holds a `List<TurnBlock>`. Each `TurnBlock` records the role, accumulated raw text, and a `Rendered` flag. The view redraws from the list on any change.

```
record TurnBlock(ChatRole Role, string RawText, bool Rendered)
```

**Alternatives considered:**
- *Append raw strings to a single `TextView`*: Simple but makes settle-rendering (replacing streamed content with Markdig output) impractical — no way to identify which range of text belongs to the current turn.
- *One `TextView` per turn*: Clean isolation but Terminal.Gui layout overhead for long sessions.

**Rationale:** The block list is a natural model for a chat conversation. Redrawing from a list is fast because Terminal.Gui only paints visible rows. The `Rendered` flag is the only state needed to drive the stream→settle transition.

---

### Decision: Hybrid streaming/settled rendering

**Chosen:** Stream raw tokens appended to the current `TurnBlock.RawText`. On each append, scan the tail of the buffer for completed inline code spans (backtick pairs) and apply monospace colour to those ranges immediately. On `TurnEndEvent`, set `Rendered = true`, parse the full buffer with Markdig, walk the AST to produce `(string, Attribute)` segments, and redraw the block with full styling.

**Alternatives considered:**
- *Stream raw, never settle*: Simple, but code blocks and bullets look unpolished.
- *Buffer full blocks, render on block completion*: Better fidelity during streaming, but requires a streaming block-boundary detector. Code blocks (fenced with ` ``` `) span multiple events; detecting boundaries reliably is fiddly.
- *Settle only (no streaming display)*: User sees nothing until the turn ends. Unacceptable UX for long responses.

**Rationale:** The hybrid gives visible streaming activity (the most important UX requirement) with a clean settled state at turn end. The visual transition is subtle because prose text is unchanged — only structural elements (code blocks, lists) visibly "settle". The Markdig parse is O(n) in turn length and happens once per turn, not per token.

---

### Decision: Step-dialog wizard

**Chosen:** The setup wizard is a list of `WizardStep` delegates, each `Func<WizardState, Task<WizardState?>>` (returns `null` to cancel). The runner iterates the list, passing state forward; Back is supported by keeping a stack of prior states and re-running from the popped position.

**Rationale:** Each step is independently testable and replaceable. Provider extensions can inject additional steps into the sequence at a well-defined position. Back navigation falls out naturally from the stack. No monolithic wizard class to modify when adding a step.

---

### Decision: Thread safety via Application.MainLoop.Invoke

The `EventDispatcher` continues running on a background `Task`, reading from the `ChannelReader<Event>`. Every handler that touches a Terminal.Gui view marshals the update via `Application.MainLoop.Invoke(() => { ... })`. No shared mutable state is accessed from the background task except through this marshal.

## Risks / Trade-offs

**Risk: Terminal.Gui v2 API surface is still evolving** → Pin to a specific minor version in the `.csproj`. Review release notes on upgrade.

**Risk: TextView redraw-from-list performance for very long sessions** → Acceptable for V1. If sessions routinely exceed several hundred turns, introduce a view-window that only holds the last N blocks in memory.

**Risk: Markdig adds a dependency** → Markdig is a well-maintained, widely used .NET markdown parser (MIT). The risk is low. If it proves problematic, the renderer can be replaced without changing the `TurnBlock` model.

**Risk: Settle transition is visible** → In practice the only elements that change appearance are fenced code blocks and list items. Plain prose is identical pre- and post-render. User testing will determine if this is perceptible enough to matter.

## Migration Plan

1. Add `Dmon.Tui` project to solution; remove `Dmon.Console`
2. Implement `DmonWindow`, `ChatOutputView`, `TuiEventHandler`, step-dialog wizard
3. Port all slash command and event-handling logic from `ConsoleHost` to `TuiEventHandler`
4. Wire `EventDispatcher` output to `TuiEventHandler` via `Application.MainLoop.Invoke`
5. Build and verify `dotnet build` passes with zero warnings
6. Manual smoke test: startup → submit message → verify response renders → tool confirm → `/exit`

No rollback strategy required — this is a host-only change. The core process and protocol are unchanged.

## Open Questions

- Should the status bar show token count / cost estimates? (Deferred — no cost data in current protocol events)
- Should the settled render preserve the user's scroll position? (Assume yes; Terminal.Gui scroll state should be preserved if view is not reconstructed)
