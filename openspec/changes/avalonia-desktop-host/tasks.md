## 1. Project scaffold and dependencies

- [x] 1.1 Create `frontends/Dmon.Desktop/Dmon.Desktop.csproj` (net10.0, `OutputType=Exe`, Avalonia app; no `UseWpf`), referencing `core/Dmon.Runtime` and `core/Dmon.Protocol` via `ProjectReference`.
- [x] 1.2 Add package versions to `Directory.Packages.props`: `Avalonia`, `Avalonia.Desktop`, `ReactiveUI.Avalonia` (the renamed `Avalonia.ReactiveUI`; provides `RoutedViewHost`/`UseReactiveUI`, the only package with an Avalonia 12 build), `Avalonia.Themes.Fluent` (base), `Pipboy.Avalonia`, `Pipboy.Avalonia.Fx`, `Markdown.Avalonia`, `Splat.Microsoft.Extensions.DependencyInjection`; reference them (versionless) from the csproj. The whole stack sits on the Avalonia 12 line (Pipboy requires Avalonia ≥ 12); `Markdown.Avalonia` on Avalonia 12 is currently prerelease (`12.0.0-a*`).
- [x] 1.3 Register `frontends/Dmon.Desktop` in `frontends.slnx` and `Everything.slnx`; confirm `dotnet build` resolves the project.
- [x] 1.4 Add the Avalonia app entry point (`Program.cs` with `BuildAvaloniaApp().UseReactiveUI()`) and `App.axaml`/`App.axaml.cs` applying `<pipboy:PipboyTheme />`; verify a blank window launches.

## 2. Composition root and DI bridge

- [x] 2.1 Build the MS DI composition root for the app (services + view-models + `IViewFor` views) and bridge ReactiveUI's Splat locator to it via `Splat.Microsoft.Extensions.DependencyInjection`.
- [x] 2.2 Register views explicitly as `IViewFor<TViewModel>` (no convention-only scanning); add a smoke test asserting a `RoutedViewHost` resolves a view for a routed view-model through the container.

## 3. Core lifecycle and RPC wiring

- [x] 3.1 Implement a session service that launches the local core via `ICoreLauncher.StartProtocolCompatibleCoreAsync` and builds an `IRpcClient` over `CoreProcessRpcTransport`, calling `StartAsync` before any command.
- [x] 3.2 Bridge `IRpcClient.Events` to an `IObservable<Event>` and `ObserveOn(RxApp.MainThreadScheduler)`; route events to view-model state. Verify (unit test) that state mutation happens only after the scheduler hop.
- [x] 3.3 Gate interaction on `agentReady`: present a boot/startup state (optionally `Pipboy.Avalonia.Fx`) until the launcher's protocol-compat handshake is observed, then enable the conversation UI.
- [x] 3.4 Implement clean teardown (dispose `IRpcClient`, stop the core) on window close and cancellation.

## 4. ReactiveUI routing shell

- [x] 4.1 Implement `SessionViewModel : ReactiveObject, IScreen` owning a `RoutingState Router`; host one instance in a top-level `RoutedViewHost`.
- [x] 4.2 Implement `ConversationViewModel` as the default routed screen and navigate to it on startup.
- [x] 4.3 Add a unit test asserting `SessionViewModel.Router` initializes with `ConversationViewModel` as the current screen.

## 5. Conversation rendering (parts + streaming)

- [x] 5.1 Model the message list as a DynamicData `SourceList`/`SourceCache` transformed to message view-models, bound to a `ReadOnlyObservableCollection` on `ConversationViewModel`.
- [x] 5.2 Add Avalonia `DataTemplates` selected by `Part` subtype: `TextPart`, `ToolCallPart`, `ToolResultPart`, `ImagePart`, `ReasoningPart`, `UnknownPart` (render-only).
- [x] 5.3 Render `TextPart` markdown with `Markdown.Avalonia`, themed to the PipBoy phosphor/monospace palette.
- [x] 5.4 Stream `messageDelta` into the in-progress assistant message; coalesce the delta stream with `Buffer`/`Sample` over a short window; settle the turn on the turn-end event. Unit-test that a burst of deltas collapses to bounded UI updates and the settled text matches.
- [x] 5.5 Assert (unit test) that `UnknownPart` is rendered but never included in any outbound command.

## 6. Input and commands

- [x] 6.1 Implement the prompt input + `SendPrompt` `ReactiveCommand` calling `IRpcClient.SendAsync`; bind `CanExecute` to an `IsStreaming` observable so send is disabled mid-turn.
- [x] 6.2 Implement tool-confirmation via a ReactiveUI `Interaction<,>`: modal shows tool name/args/risk (high-risk visually distinct) with allow-once/allow-project/allow-global/deny, and relays the confirm-response command. Unit-test the VM raises the interaction and maps the result to the correct command.
- [x] 6.3 Implement UI-input requests via an input `Interaction<,>`, relaying the response to the core.
- [x] 6.4 Implement the reload action (dispose client + relaunch core + rebind + re-open session dir), allowed only between turns and rejected during streaming. Unit-test the between-turns guard.

## 7. Docs and scope-gate amendment

- [x] 7.1 Amend `coding-agent-brief.md` ("Out of Scope for V1" + "Avalonia UI Possibilities") to record the gating precondition is met and the Avalonia host is in scope; note multi-tab + V1.5+ affordances remain deferred.
- [x] 7.2 Update the `CLAUDE.md` "Out of scope for V1" mirror to match.
- [x] 7.3 Add a `frontends/Dmon.Desktop/README.md`: prerequisites (populated `dmoncore` cache), how to run, the runtime-resolve core-acquisition note, and the deferred-installer caveat.

## 8. Gates and live verification

- [x] 8.1 `make build` clean under `TreatWarningsAsErrors`; `make test` green (new desktop view-model/unit tests + all existing tests).
- [x] 8.2 `openspec validate avalonia-desktop-host --strict` passes.
- [x] 8.3 Live verification (human-in-the-loop, copy-pasteable recipe): launch `Dmon.Desktop` against a live core with a configured provider; confirm the boot→agentReady gate, a streamed turn renders markdown, a tool-confirmation modal round-trips, and reload restarts the core between turns. Confirm no `Dmon.Core`/`Dmon.Protocol`/`Dmon.Runtime` change was required — if any was, stop and surface it as an RPC-surface-leak finding. **Verified 2026-06-17 against a live Gemini core: boot→agentReady gate, streamed PipBoy-themed markdown, tool-confirm modal round-trip, and reload (button + Ctrl+R) all confirmed. Zero core/protocol/runtime changes — the RPC surface held. Live findings (text-only `{role,content}` turns, resolver-swap platform re-registration, two-scheduler coalescing, Consolas font, core cwd) recorded in design.md "Live-verification findings".**
