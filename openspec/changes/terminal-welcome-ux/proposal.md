## Why

The Terminal host's input zone is asymmetric and its readiness signal is broken: the model/state separator sits only *above* the editor (dcli previously had no surface below-the-preamble or prompt glyph), the live input line has no prompt affordance, and the `[Ready]` banner reports `dmon core v0.0.0.0` because the core reads its numeric `AssemblyVersion` rather than the stamped informational version. dcli `0.3.0-rc.0` now ships the two surfaces this redesign was waiting on — a persistent input preamble (rows pinned *above* the editor) and an inline input prompt prefix — so the held "option C" symmetric-frame design can finally land.

## What Changes

- Bump the central `Dcli` and `Dcli.Testing` package pins from `0.2.0-rc.4` to `0.3.0-rc.0` in `Directory.Packages.props`.
- Write an ASCII `dmon` banner + tagline MOTD to scrollback on startup (replacing the bare `dmon` separator), so the session opens with an identifiable welcome.
- Render a `── dmon ──` rule pinned **above** the input editor via dcli's new `InputPreamble` surface (`IInputPreamble`), rather than as a `Status` row above the editor.
- Render a `❯ ` prompt glyph on the live input line via dcli's new `Input.SetPrompt`.
- Render a `────` rule + a `[Ready] dmon core v{version} {model}` line pinned **below** the input via `Status.SetRows`, replacing today's model-only status row and folding the readiness/version signal into the persistent frame (the protocol string is dropped from the pinned row in favour of the active model name).
- Fix the version defect: the core SHALL report its stamped informational version (via `AssemblyInformationalVersionAttribute`, already produced by the wired-in MinVer) in `agentReady.coreVersion`, instead of the numeric `Assembly.GetName().Version` that renders as `0.0.0.0`.

## Capabilities

### New Capabilities

_None._ This change modifies the existing terminal-host presentation and the core's version reporting; it introduces no new capability.

### Modified Capabilities

- `terminal-host`: The input-zone framing requirement changes — the top separator/title moves from a `Status` row to the new dcli `InputPreamble` surface, the input editor gains a `❯` prompt prefix via `Input.SetPrompt`, and the pinned `Status` row below the editor carries a `[Ready] dmon core v{version} {model}` readiness line. A new requirement covers the startup `dmon` banner + tagline MOTD. The dcli-substrate mapping is extended to include the `InputPreamble` and `SetPrompt` surfaces.
- `agent-core`: The `agentReady.coreVersion` value requirement is refined — it SHALL be the core's stamped informational/package version, not the numeric four-part `AssemblyVersion`, so the readiness line never reports `0.0.0.0`.

## Impact

- **Dependencies:** `Directory.Packages.props` — `Dcli` / `Dcli.Testing` `0.2.0-rc.4` → `0.3.0-rc.0` (consumes the new `IInputPreamble` and `Input.SetPrompt` API surface).
- **Terminal host (`frontends/Dmon.Terminal`):** `TerminalRenderer.cs` (status row content, new preamble + prompt-prefix wiring, banner/MOTD emit), `Program.cs` (startup banner and `[Ready]` line relocation; feed `agentReady.CoreVersion` into the renderer's status), `ConsoleEventHandler.cs` (status now carries version alongside model/state).
- **Core (`core/Dmon.Core`):** `Rpc/RpcHostedService.cs` (read `AssemblyInformationalVersionAttribute` for `coreVersion`).
- **Tests (`test/Dmon.Terminal.Tests`):** `TerminalRendererHarnessTests` / `ConsoleEventHandlerTests` / `FakeTerminal` updated for the preamble surface, prompt prefix, and version-bearing status row; a core-side assertion that `coreVersion` is the stamped informational version.
- **No wire-contract change:** the `agentReady` event shape (`{protocolVersion, coreVersion}`) is unchanged; only the `coreVersion` value's derivation changes.
