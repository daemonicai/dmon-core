## Why

The brief always planned two host surfaces — a console/TUI host and an Avalonia desktop host — but gated the desktop host behind a sequencing precondition: *"build the console host first, prove the RPC surface."* That precondition is now met: `Dmon.Terminal` ships, and the host-facing RPC surface (`IRpcTransport`/`IRpcClient`/`ICoreLauncher`/`ICoreProcess`) was extracted into `Dmon.Runtime` and is already shared by two consumers (Terminal and Gateway). A second, independent GUI frontend is both the long-promised desktop affordance and the best available proof that the RPC surface is genuinely host-agnostic.

## What Changes

- Add a new frontend project `frontends/Dmon.Desktop`: an Avalonia desktop application that is a **thin host over `Dmon.Runtime`**, spawning a local core over stdio exactly as `Dmon.Terminal` does ("TUI-with-pixels"). Shares all agent behaviour, zero shared UI code.
- **First cut is single-window / single-session parity** with the TUI: render the conversation `Part[]` (text/tool-call/tool-result/image/reasoning/unknown), stream `MessageDelta` deltas and settle on `MessageEnd`, answer `ToolConfirmRequest`/`UiInputRequest` with dialogs, and support `/reload` via dispose + relaunch.
- MVVM with **ReactiveUI**, including **full routing from the start** (`SessionViewModel : IScreen` with its own `RoutingState`), so the later multi-tab shell and in-session screens (settings, session-graph, extension browser) are additive rather than a retrofit.
- Theme via the **`Pipboy.Avalonia`** library (+ `Pipboy.Avalonia.Fx` for an `agentReady` boot sequence); markdown via **`Markdown.Avalonia`**, themed to the PipBoy palette.
- Core acquisition reuses the existing **runtime-resolve from the NuGet cache** path (same as the `dmon` dotnet tool). No new distribution mechanism and **no new ADR**.
- **Multi-core / multi-tab is explicitly deferred to a second change.** It is free at the `Dmon.Runtime` layer (per-instance launcher/client) and is captured here only as the non-goal the first cut is designed to grow into.
- Amend `coding-agent-brief.md` (and the `CLAUDE.md` "Out of Scope for V1" mirror): record that the Avalonia host's gating precondition is satisfied and move it from deferred into active scope. *(Documentation amendment, not an ADR or spec change.)*

## Capabilities

### New Capabilities
- `desktop-host`: An Avalonia desktop frontend that hosts a single local core over `Dmon.Runtime`, renders the conversation parts model with streaming, handles host-directed input requests (tool confirmations / UI input), and is built on ReactiveUI routing so multi-session tabs are an additive future step.

### Modified Capabilities
<!-- None. The desktop host consumes the existing host-rpc-transport and
     core-runtime-acquisition behaviours unchanged; no standing-spec requirements change. -->

## Impact

- **New project:** `frontends/Dmon.Desktop` (net10.0, `OutputType=Exe`, Avalonia + `ReactiveUI.Avalonia` + `Avalonia.Desktop`), added to `frontends.slnx` and `Everything.slnx`.
- **New dependencies (Directory.Packages.props):** `Avalonia`, `Avalonia.Desktop`, `ReactiveUI.Avalonia`, `Pipboy.Avalonia`, `Pipboy.Avalonia.Fx`, `Markdown.Avalonia`, `Splat.Microsoft.Extensions.DependencyInjection` (bridges ReactiveUI's Splat locator to the project's MS DI for both services and `IViewFor` view resolution). `ReactiveUI.Avalonia` is the renamed `Avalonia.ReactiveUI`; it is the only ReactiveUI-Avalonia integration with an Avalonia 12 build (12.0.3), and `Pipboy.Avalonia` requires Avalonia ≥ 12, so the whole stack is pinned to the Avalonia 12 line. `Markdown.Avalonia`'s Avalonia 12 build is currently prerelease.
- **Consumes unchanged:** `Dmon.Runtime` (`ICoreLauncher`/`IRpcClient`), `Dmon.Protocol` (events, commands, parts). No core, runtime, or protocol changes expected — if any prove necessary, that is the headline finding of the change (the RPC surface leaked) and must be surfaced, not patched silently.
- **Docs:** `coding-agent-brief.md` and `CLAUDE.md` scope sections amended.
- **Release matrix:** the desktop host is an *app artifact* (ADR-024/025 app-artifact family), not a NuGet package; packaging/installer is out of scope for this first cut (resolve-at-runtime assumes a populated cache, as on a dev machine).
