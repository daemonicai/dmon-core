## ADDED Requirements

### Requirement: Desktop host is a thin frontend over `Dmon.Runtime`

The Avalonia desktop host (`Dmon.Desktop`) SHALL host the agent by spawning a **local** `Dmon.Core` process and communicating over JSONL/stdio through `Dmon.Runtime`'s host-facing surface — `ICoreLauncher.StartProtocolCompatibleCoreAsync` to launch and protocol-gate the core, and `IRpcClient` (over `CoreProcessRpcTransport`) to send commands and consume events. It SHALL NOT re-implement framing, request/response correlation, or protocol-version gating, and SHALL NOT reference `Dmon.Core` internals; it depends only on `Dmon.Runtime` and `Dmon.Protocol`. It SHALL share zero UI code with `Dmon.Terminal`.

The first cut SHALL host exactly one session (one core process) per window. Multi-session / multi-core tabbing is out of scope for this change.

#### Scenario: Local core spawned via the runtime launcher

- **WHEN** the desktop host starts
- **THEN** it calls `ICoreLauncher.StartProtocolCompatibleCoreAsync` to spawn and protocol-gate a local core, then builds an `IRpcClient` over the returned process and calls `StartAsync` before sending any command

#### Scenario: No core or protocol internals re-implemented

- **WHEN** the `Dmon.Desktop` project is built
- **THEN** its project references include `Dmon.Runtime` and `Dmon.Protocol` and do NOT include `Dmon.Core`, and the host contains no JSONL framing or command-correlation logic of its own

#### Scenario: Single session per window

- **WHEN** the first cut of the desktop host is running
- **THEN** exactly one core process and one `IRpcClient` are live, and there is no UI affordance to open a second concurrent session

### Requirement: Core acquisition reuses the runtime-resolve path

The desktop host SHALL acquire the `dmoncore` engine through the **same runtime-resolve-from-NuGet-cache** mechanism used by the `dmon` dotnet tool (via `ICoreLauncher`). It SHALL NOT introduce a new bundling, pinning, or distribution mechanism. Producing an installable, self-contained desktop artifact that ships its own core is out of scope for this change.

#### Scenario: Core resolved from the cache like the dotnet tool

- **WHEN** the desktop host launches the core
- **THEN** the core path is resolved by the existing `ICoreLauncher` resolution, with no desktop-specific bundling step

### Requirement: ReactiveUI MVVM with routing from the start

The desktop host SHALL use ReactiveUI as its MVVM framework. The per-session view-model SHALL implement `IScreen` and own its own `RoutingState`, so that in-session navigation (e.g. a future settings, session-graph, or extension-browser screen) is performed via `Router.Navigate` and the later multi-session shell is an additive wrapper rather than a retrofit. The first cut SHALL host one `SessionViewModel` (as the `IScreen`) in a top-level `RoutedViewHost`, with `ConversationViewModel` as the default routed screen.

View resolution SHALL go through `IViewFor<TViewModel>` registrations, and ReactiveUI's Splat locator SHALL be bridged to the project's `Microsoft.Extensions.DependencyInjection` container (so services and `IViewFor` views resolve from one inspectable composition root, consistent with the rest of dmon).

#### Scenario: Session view-model is an IScreen with its own router

- **WHEN** the desktop host constructs the session
- **THEN** `SessionViewModel` exposes a `RoutingState Router`, the top-level view binds a `RoutedViewHost` to it, and `ConversationViewModel` is the initial routed screen

#### Scenario: View resolution flows through MS DI

- **WHEN** ReactiveUI resolves a view for a view-model via `IViewFor<TViewModel>`
- **THEN** the instance is provided by the Microsoft.Extensions.DependencyInjection container through the Splat bridge, not by convention-only assembly scanning

### Requirement: Streaming output renders in real time on the UI thread

The desktop host SHALL display `messageDelta` tokens as they arrive. Inbound events from `IRpcClient.Events` SHALL be observed on the UI dispatcher (ReactiveUI's `RxApp.MainThreadScheduler`) before they mutate view-model state, so the background event pump never touches Avalonia controls directly. To avoid flooding the dispatcher under fast token streams, the `messageDelta` stream SHALL be coalesced (e.g. `Buffer`/`Sample` over a short window) before the per-token UI update. The composing input SHALL be disabled while a turn is streaming and re-enabled when the turn ends.

#### Scenario: Tokens appear as they stream

- **WHEN** the core emits a sequence of `messageDelta` events during a turn
- **THEN** the in-progress assistant message updates visibly before the turn ends, with updates marshalled onto the UI dispatcher

#### Scenario: Event pump does not touch UI off-thread

- **WHEN** an event is received on the background `IRpcClient` pump
- **THEN** view-model state bound to the UI is mutated only after an `ObserveOn(RxApp.MainThreadScheduler)` hop, never directly from the pump task

#### Scenario: Send disabled mid-turn

- **WHEN** a turn is streaming
- **THEN** the send command's `CanExecute` is false and re-becomes true once the turn-end event is observed

### Requirement: Conversation parts render via type-selected templates

The desktop host SHALL render a conversation message's `Part[]` (per the ADR-016 parts model) using Avalonia `DataTemplates` selected by `Part` subtype: `TextPart` as themed markdown (see the markdown requirement), `ToolCallPart` and `ToolResultPart` as tool-call/output presentations, `ImagePart` as an image, `ReasoningPart` as a (collapsible) reasoning presentation, and `UnknownPart` as a render-only fallback that is never sent back to the model. The message list SHALL be maintained as an observable collection (e.g. DynamicData `SourceList`/`SourceCache` transformed to view-models) bound to the conversation view.

#### Scenario: Each part type has a template

- **WHEN** a message containing text, a tool call, a tool result, and a reasoning part is rendered
- **THEN** each part is presented by the template registered for its subtype

#### Scenario: Unknown parts are render-only

- **WHEN** a message contains an `UnknownPart`
- **THEN** it is displayed in a fallback presentation and is never included in any command sent back to the core

### Requirement: Markdown rendered with `Markdown.Avalonia`, themed to PipBoy

Settled assistant text (`TextPart`) SHALL be rendered as markdown using the `Markdown.Avalonia` library, styled to match the PipBoy theme (monochromatic phosphor palette, monospace). The host SHALL NOT port the terminal host's `dcli`-based markdown renderer.

#### Scenario: Markdown renders with theme styling

- **WHEN** an assistant message containing markdown (headings, emphasis, fenced code, links) is settled
- **THEN** it is rendered by `Markdown.Avalonia` with the PipBoy phosphor/monospace styling applied

### Requirement: Tool-confirmation and UI-input requests use ReactiveUI Interactions

The desktop host SHALL handle host-directed input events — `tool.confirmRequest` and UI input requests — via ReactiveUI `Interaction<,>`: the session view-model raises the interaction, the view's registered handler presents a modal dialog, and the user's response is relayed back to the core as the corresponding response command. Tool-confirmation dialogs SHALL present the tool name, arguments, and risk level, and SHALL offer the same response scopes the protocol defines (allow once / allow for project / allow globally / deny). High-risk requests SHALL be visually distinct.

#### Scenario: Tool confirmation prompts via interaction

- **WHEN** the core emits `tool.confirmRequest`
- **THEN** the session view-model raises a confirmation `Interaction`, the view shows a modal presenting tool name, args, and risk, and the chosen response (with scope) is sent back to the core as the confirm-response command

#### Scenario: High-risk request is visually distinct

- **WHEN** the request's risk is high
- **THEN** the dialog renders a distinct high-risk indication before the option list

#### Scenario: UI input request round-trips

- **WHEN** the core emits a UI input request
- **THEN** the view-model raises an input `Interaction`, the view collects the input, and the response is relayed to the core

### Requirement: `/reload` restarts the core between turns

The desktop host SHALL provide a reload action that restarts the core to re-read configuration: dispose the current `IRpcClient`, relaunch the core via `ICoreLauncher`/`ICoreProcess` (the Terminal restart pattern), rebind the event subscription to the fresh process, and re-open the active session directory. Reload SHALL run only between turns, never during an active streaming turn.

#### Scenario: Reload relaunches and rebinds

- **WHEN** the user triggers reload while idle
- **THEN** the previous core is stopped, a fresh core is launched, and the host consumes events from and sends commands to the new process

#### Scenario: Reload rejected during streaming

- **WHEN** reload is triggered during an active streaming turn
- **THEN** the restart does not occur until the turn completes

### Requirement: PipBoy theme and an agentReady boot sequence

The desktop host SHALL apply the `Pipboy.Avalonia` theme in `App.axaml` (`<pipboy:PipboyTheme />`). The host SHALL gate interaction on the runtime `agentReady` handshake (the protocol-compat gate surfaced by `ICoreLauncher`): the conversation UI becomes interactive only after `agentReady` is observed. While the core is starting and until `agentReady`, the host SHALL present a startup ("boot") state, which MAY use `Pipboy.Avalonia.Fx` effects.

#### Scenario: Theme applied

- **WHEN** the application starts
- **THEN** the PipBoy theme is applied via `App.axaml` and standard Avalonia controls render in the PipBoy palette

#### Scenario: Interaction gated on agentReady

- **WHEN** the core has been launched but `agentReady` has not yet been observed
- **THEN** the host shows the boot/startup state and the send command is not enabled; once `agentReady` is observed the conversation UI becomes interactive

### Requirement: Desktop host project placement and build

The desktop host SHALL live at `frontends/Dmon.Desktop`, target `net10.0` with `OutputType=Exe`, and be referenced from `frontends.slnx` and `Everything.slnx`. Its package dependencies SHALL be declared through central package management (`Directory.Packages.props`) and SHALL build clean under `TreatWarningsAsErrors`.

#### Scenario: Project participates in the solutions and builds clean

- **WHEN** the solution is built in Release
- **THEN** `frontends/Dmon.Desktop` is included via `frontends.slnx`/`Everything.slnx` and compiles with no warnings under `TreatWarningsAsErrors`
