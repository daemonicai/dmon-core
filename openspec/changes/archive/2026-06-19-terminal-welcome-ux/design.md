## Context

The Terminal host (`frontends/Dmon.Terminal`) renders over the `dcli` TUI library. `dcli`'s fixed region is a vertical stack: an optional **preamble** (rows above the editor), the **input editor**, and the **status** rows (below the editor). Until now `dcli` had no preamble surface and no input-prompt prefix, so the current renderer fakes a frame by pushing the *top* separator (model + state) into `Status.SetRows` rows that happen to sit above where it draws, and there is no `❯` glyph on the editor line. The readiness/version string lives only as a one-shot scrollback line emitted at startup (`Program.cs`), and it reads `v0.0.0.0` because the core derives `agentReady.coreVersion` from the numeric four-part `Assembly.GetName().Version` rather than the stamped informational version.

`dcli 0.3.0-rc.0` adds the two surfaces this redesign was waiting on:

- `ITerminal.InputPreamble` → `IInputPreamble.SetRows(params Line[] | IReadOnlyList<Line>)` — styled rows pinned directly **above** the editor.
- `ITerminal.Input.SetPrompt(Line)` / `SetPrompt(string)` — an inline prefix on the editor's first row.

`ITerminal.Status.SetRows` is unchanged and continues to pin rows **below** the editor.

Current code touchpoints (from the host map):
- `TerminalRenderer.cs` — `SetStatus(string modelName, bool thinking)` → `RefreshStatus()` builds one grey `Status.SetRows` row (`"{model} · thinking…"` or `"{model}"`). Holds `_modelName`, `_thinking`. No version field.
- `ConsoleEventHandler.cs` — calls `SetStatus(_modelName, …)` on `TurnStart`/`TurnEnd`/`ProviderSwitched`; `_modelName` is sourced from `ProviderSwitchedEvent.Model`.
- `Program.cs` — `PrintSeparator("dmon")` then `AddSystemLine($"[Ready] dmon core v{ready.CoreVersion} (protocol {ready.ProtocolVersion})")`; `AgentReadyEvent` is already on hand (`coreSession.AgentReady`). A symmetric `[Reload]` line exists.
- `core/Dmon.Core/Rpc/RpcHostedService.cs:38` — `coreVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0"`. MinVer is already wired as a `GlobalPackageReference` (so `AssemblyInformationalVersionAttribute` is stamped at build).
- Tests: `test/Dmon.Terminal.Tests` — `TerminalRendererHarnessTests` (real `HeadlessTerminal`), `ConsoleEventHandlerTests`, and a hand-written `Fakes/FakeTerminal.cs` implementing `ITerminal`.

## Goals / Non-Goals

**Goals:**
- Land the held "option C" symmetric frame: banner/MOTD in scrollback, `── dmon ──` rule via `InputPreamble`, `❯ ` prompt via `Input.SetPrompt`, `────` rule + `[Ready] dmon core v{version} {model}` via `Status.SetRows`.
- Make `agentReady.coreVersion` report the stamped informational version so the readiness line shows a real version (e.g. `0.2.0-preview.23`) and never `0.0.0.0`.
- Keep the renderer testable through the existing `FakeTerminal` / `HeadlessTerminal` seams.

**Non-Goals:**
- No change to the `agentReady` wire shape (`{protocolVersion, coreVersion}`) — only the `coreVersion` value's derivation.
- No new `dcli` capabilities — this consumes the already-shipped `0.3.0-rc.0` surfaces.
- No change to streaming/markdown-settle rendering, wizard, or tool-confirm flows.
- No banner theming/animation beyond a static ASCII banner + tagline.

## Decisions

### D1 — Frame layout maps directly onto dcli's three fixed-region bands
Top rule → `InputPreamble.SetRows`; editor prefix → `Input.SetPrompt("❯ ")` (set once at startup, it persists across turns and is not an `InputChanged` trigger); bottom rule + readiness → `Status.SetRows([rule, readiness])`. This replaces the current single-row status hack. *Alternative considered:* keep everything in `Status.SetRows` and skip the preamble — rejected because it cannot pin a rule *above* the editor, which is the whole point of the symmetric frame and why the change was held for `0.3.0-rc.0`.

### D2 — The renderer owns the readiness string; version is pushed in once
`TerminalRenderer` gains a `_coreVersion` field set by a new entry point (e.g. `SetReadiness(string coreVersion)` or an extra parameter threaded from `Program.cs` where `coreSession.AgentReady` is already available). `RefreshStatus()` then composes the bottom band as `[rule]` + `[Ready] dmon core v{_coreVersion} {_modelName}` (with the `· thinking…`/idle indicator folded into the same line or appended). *Alternative considered:* have `ConsoleEventHandler` format the readiness line — rejected; status composition already lives in `TerminalRenderer.RefreshStatus`, so keeping all band composition there preserves a single source of truth and one place for the harness to assert.

### D3 — Drop the protocol string from the pinned row, keep model
The pinned readiness row shows version + active model (`{model}` from `ProviderSwitchedEvent`), not the protocol number, per the proposal. The protocol version remains available in the startup/`[Reload]` scrollback lines if we keep them; the pinned frame favours the model name the user cares about turn-to-turn.

### D4 — Banner/MOTD is a startup scrollback emit, not a fixed-region band
The ASCII `dmon` banner + tagline are written once to scrollback at startup via the renderer (replacing `PrintSeparator("dmon")`), so they scroll away naturally and don't consume fixed-region height. *Alternative:* pin the banner in the preamble — rejected; the preamble is reserved for the persistent `── dmon ──` rule and a tall banner there would permanently eat vertical space.

### D5 — Version fix reads `AssemblyInformationalVersionAttribute`
`RpcHostedService` reads `Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion`, falling back to the numeric version then `"0.0.0"`. MinVer already stamps the attribute, so no csproj change is required beyond confirming the stamp reaches the core assembly. *Alternative considered:* expose a dedicated `ProductVersion`/`ThisAssembly` constant — rejected as redundant given MinVer is already in the build.

### D6 — `FakeTerminal` grows an `InputPreamble` surface
The hand-written `Fakes/FakeTerminal.cs` must implement the new `ITerminal.InputPreamble` member and record `SetRows`/`SetPrompt` calls so unit tests can assert the preamble rule, the prompt glyph, and the version-bearing status row. `HeadlessTerminal` from `Dcli.Testing 0.3.0-rc.0` already provides these surfaces for the integration harness.

## Risks / Trade-offs

- [The `0.2.0-rc.4` → `0.3.0-rc.0` pin is a minor bump, not the rc.5/rc.6 the held design anticipated] → Verify at restore/build that the `IInputPreamble` and `Input.SetPrompt` members resolve against `0.3.0-rc.0`; the `make build` gate catches any API drift.
- [MinVer may not stamp `InformationalVersion` in all build/run modes, leaving the readiness line blank or `0.0.0`] → Keep the numeric/`"0.0.0"` fallback chain so the line is never empty, and assert the attribute-read path in a core-side test; surface the limitation if the stamp is absent under `--no-build` run.
- [Fixed-region height grows by the preamble rule row, reducing scrollback rows on very short terminals] → The preamble is a single rule row; acceptable, and consistent with `dcli`'s documented fixed-region budgeting.
- [`FakeTerminal` drift — adding a new `ITerminal` member can silently break unrelated tests that construct it] → Implement the member with a recording stub mirroring the existing surfaces; the compiler enforces completeness.

## Open Questions

_None._ The held design is now fully unblocked by `dcli 0.3.0-rc.0`; all surfaces and the version source are confirmed present.
