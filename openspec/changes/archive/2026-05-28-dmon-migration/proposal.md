## Why

`Dmon.Terminal` today is built on Spectre.Console. That choice is the root cause of every untested file in the project — Spectre's static `AnsiConsole` singleton can't be substituted, so the `WizardEngine` is the only file with unit tests (hand-quarantined behind an injected `Func<WizardStep, Outcome>` delegate). The new `dcli` library was built specifically to fix that — its façade is `ITerminal`, every call site is fakeable (tier A), and a headless harness (`Dcli.Testing`) drives the real loop and layout terminal-free (tier B). This change migrates `Dmon.Terminal` onto `dcli` so the whole terminal surface becomes unit-testable, the inline-scrollback / awaitable-dialog / autocomplete features ship for free, and the Spectre.Console dependency can be removed.

The migration also closes a strategic loop: `dcli` was scoped against `Dmon.Terminal`'s requirements (Decision 3: dcli = mechanics + rendering; dmon = data + semantics). Until this migration lands, the scoping is a hypothesis. After it lands, dmon's terminal layer becomes the first real consumer and validates the design end-to-end.

This change depends on `dcli`'s `api-ergonomics-pass-1` change being released first (as version `dcli 0.2.0-rc.x` or later). Without it, `MarkdownRenderer` cannot complete the port cleanly (it would still emit Spectre markup strings rather than `Line[]`), blocking the Spectre removal step.

## What Changes

- **Port to `dcli` `ITerminal` surface (7 files):**
  - `TerminalRenderer.cs` — token streaming, separator rules, status lines → `Scrollback.BeginLive().AppendText`/`Commit`, `Scrollback.Append`, `Status.SetRows`.
  - `InlinePrompt.cs` — collapses into the `dcli` `SelectAsync` / `InputAsync` dialogs.
  - `ToolConfirmPrompt.cs` → `ChoiceAsync`.
  - `WizardEngine.cs` — drop the injected `Func<WizardStep, Outcome>` test-quarantine indirection; call `dcli` dialogs directly. **Back-stack semantics preserved byte-for-byte** (the validated §14.4 port pattern).
  - `ConsoleEventHandler.cs` — becomes a thin adapter that translates RPC events to `dcli` calls (`ToolConfirmRequest` → `ChoiceAsync`, `UiInputRequest` → `InputAsync`, wizard step → `SelectAsync`/`InputAsync` dispatch).
  - `InputReader.cs` — refactored: history + IsLocked state remain in dmon (a state layer above dcli); the raw byte loop and timing become `dcli`'s responsibility. Subscribes to `dcli.Events.InputSubmitted` / `InputChanged`.
  - `MarkdownRenderer.cs` — output type changes from `string` (Spectre markup) to `IReadOnlyList<Line>`; uses `Line.FromText` / `LineBuilder` from the ergonomics-pass-1 surface.

- **Delete (no replacement needed):**
  - `WizardRenderer.cs` — its only job was rendering steps via Spectre + `InlinePrompt`; dispatch moves into `ConsoleEventHandler`.
  - `AddProviderCommand.cs`, `ReloadCommand.cs` — empty marker records whose semantics now live in the dispatch adapter.

- **Keep as-is (out of `dcli`'s scope per Decision 3):**
  - `CoreProcessManager.cs` (subprocess lifecycle), `EventDispatcher.cs` (JSONL RPC demux), `SlashCommandParser.cs` (command grammar), `ToolPermission.cs` / `WizardStepOutcome.cs` (enums), `Program.cs` structure (session orchestration; only call sites change).

- **Remove `Spectre.Console` package reference** from `Dmon.Terminal.csproj` after MarkdownRenderer is ported.
- **Add `dcli` and `Dcli.Testing` package references** (project reference if developing locally; NuGet ref once `dcli 0.2.0` ships).
- **First wave of unit tests for the previously-untestable files** — every ported file now sits behind `ITerminal` and gets either a tier-A fake-test or a tier-B headless-harness test. Targets: `TerminalRenderer`, `ConsoleEventHandler`, `InputReader` (state layer), `MarkdownRenderer` (pure-function transform), plus retain the existing `WizardEngine` tests (now driving real dialogs via the tier-A fake).

- **No behaviour change visible to the user.** The rendered output, the wizard flow, the tool-confirm prompt, the input echo, the streaming markdown — all look the same. The change is structural.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `terminal-host`: MODIFIED to specify that the terminal renderer runs on the `dcli` `ITerminal` surface (rather than Spectre.Console). The user-visible requirements (streaming output, settled markdown, inline wizard prompts, tool-confirm prompt, Ctrl+C semantics, OS copy/paste) are preserved unchanged — only the *substrate* changes. The "OS copy/paste works normally" requirement is reinforced by `dcli`'s inline-rendering decision (Decision 1).

- `console-host`: MODIFIED to specify that user-input handling, slash commands, tool-confirmation, and provider-switching go through `dcli`'s awaitable dialog methods rather than Spectre prompts. The behavioural requirements stay; the implementation contract changes.

## Impact

- **Affected code:** the entire `src/Dmon.Terminal/` project. 7 files ported, 3 deleted, 6 untouched (logic-wise; some get small adapter changes). Net LOC: expected reduction (the wizard renderer + inline prompt collapse into dcli's surface).
- **Affected tests:** `test/Dmon.Terminal.Tests/` (or wherever the existing `WizardEngine` tests live) gains coverage on every previously-untested file. The injected `Func<WizardStep, Outcome>` test seam in `WizardEngine` goes away — replaced by a fake `ITerminal`.
- **Affected packages:** `Spectre.Console` removed from `Dmon.Terminal.csproj`. `dcli` + `Dcli.Testing` added (project refs during development; NuGet refs once `dcli 0.2.0` ships).
- **Affected systems:** none external — the RPC protocol, the core subprocess, the JSONL framing, the slash command grammar, all unchanged.
- **Dependency on `dcli`:**
  - **Hard:** `dcli 0.2.0-rc.x` (release of the `api-ergonomics-pass-1` change with `Line.FromText`, string-accepting overloads, `AllowBack` flag).
  - **Soft:** any post-ergonomics dcli change that adds `Scrollback.AppendRule`, incremental `Collapsible.AppendLine`, or `PasteEvent` editor routing would let dmon shed further workaround code — out of scope here, called out as future work.
- **Out of scope:**
  - Adding new features to the terminal experience (any new UX is a separate change).
  - Changing the RPC protocol or `dmon-core` interactions.
  - The `dcli` `Input.Prompt`/`ReadOnly` deferred items — dmon will work around their absence (custom prompt rendering as a `Status` row, IsLocked-aware UI hints).
  - The `dcli` VT-escape sanitisation gap — dmon already trusts its own content sources.
