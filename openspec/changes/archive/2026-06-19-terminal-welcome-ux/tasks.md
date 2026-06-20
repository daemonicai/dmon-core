# Tasks — terminal-welcome-ux

## 1. Bump the dcli dependency

- [x] 1.1 In `Directory.Packages.props`, change the `Dcli` `PackageVersion` from `0.2.0-rc.4` to `0.3.0-rc.0`.
- [x] 1.2 In `Directory.Packages.props`, change the `Dcli.Testing` `PackageVersion` from `0.2.0-rc.4` to `0.3.0-rc.0`.
- [x] 1.3 Restore and build the solution; confirm `ITerminal.InputPreamble` (`IInputPreamble.SetRows`) and `ITerminal.Input.SetPrompt(Line|string)` resolve against `0.3.0-rc.0` (a throwaway compile reference is enough — no behaviour change in this group).

## 2. Core version stamping (`agent-core`)

- [x] 2.1 In `core/Dmon.Core/Rpc/RpcHostedService.cs`, derive `coreVersion` from `Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion`, falling back to the numeric `GetName().Version?.ToString()` and then `"0.0.0"` so the value is never empty.
- [x] 2.2 Confirm MinVer stamps `AssemblyInformationalVersionAttribute` onto the `Dmon.Core` assembly at build (already wired as a `GlobalPackageReference`); add a csproj note only if the stamp does not reach the core assembly.
- [x] 2.3 Add a core-side test asserting `agentReady.coreVersion` equals the stamped informational version when present, and falls back to the numeric/`"0.0.0"` value when the attribute is absent (per the `agent-core` `agentReady.coreVersion` requirement scenarios).

## 3. Terminal symmetric-frame redesign (`terminal-host`)

- [x] 3.1 In `TerminalRenderer`, add a startup `dmon` ASCII banner + tagline MOTD emitted to scrollback via `Scrollback.Append`, and remove the bare `PrintSeparator("dmon")` welcome use in `Program.cs`.
- [x] 3.2 Set the `── dmon ──` top rule once via `ITerminal.InputPreamble.SetRows` (above the editor), replacing the top separator currently pushed through `Status.SetRows`.
- [x] 3.3 Set the `❯ ` prompt prefix once at startup via `ITerminal.Input.SetPrompt`.
- [x] 3.4 Give `TerminalRenderer` a `_coreVersion` field set from `Program.cs` (where `coreSession.AgentReady.CoreVersion` is already available); recompose `RefreshStatus()` to emit, via `Status.SetRows`, a `────` rule row plus a `[Ready] dmon core v{version} {model}` readiness row carrying the active model and `Thinking…`/`Idle` indicator — dropping the protocol string from the pinned row.
- [x] 3.5 Update `ConsoleEventHandler` so status refreshes (`TurnStart`/`TurnEnd`/`ProviderSwitched`) keep the readiness row's version + model + state in sync.
- [x] 3.6 Decide and apply the fate of the startup/`[Reload]` scrollback `[Ready]`/`[Reload]` lines in `Program.cs` (keep for history with protocol info, or retire now that the frame carries readiness) — keep behaviour consistent between the two.
- [x] 3.7 Add the `InputPreamble` surface to `Fakes/FakeTerminal.cs` (record `SetRows`) and record `Input.SetPrompt` calls, so unit tests can assert the preamble rule, prompt glyph, and version-bearing status row.
- [x] 3.8 Update/extend unit tests (`ConsoleEventHandlerTests`, renderer unit tests) to assert: banner+tagline appended at startup; `── dmon ──` set via `InputPreamble`; `❯ ` set via `SetPrompt`; status row reads `[Ready] dmon core v{version} {model}` with state indicator and no protocol string.
- [x] 3.9 Update/extend `TerminalRendererHarnessTests` (HeadlessTerminal) to snapshot the symmetric frame (preamble rule above editor, prompt prefix, readiness row below).

## 4. Validation and gates

- [x] 4.1 `make build` clean (no warnings — `TreatWarningsAsErrors`).
- [x] 4.2 `make test` (or `env -u MEKO_API_KEY make test`) green — new and existing tests.
- [x] 4.3 `openspec validate terminal-welcome-ux --strict` passes.
