## Why

The provider-configuration wizard runs in the **Terminal** process today: `Program.cs` constructs the four concrete provider factories and `ConsoleEventHandler` drives `WizardEngine` against them locally. This is the sole reason `Dmon.Terminal` references `Dmon.Providers`, and it breaks the extension model — a provider shipped as an ADR-008 `.csx`/NuGet extension loads only into Core's `AssemblyLoadContext`, so the Terminal has no factory for it and it can never appear in the wizard. The rest of the provider flow (model listing, final config) is already RPC-driven; the interactive wizard is the holdout that still straddles the ADR-003 process boundary and duplicates provider knowledge across it.

## What Changes

- Move the wizard engine from `Dmon.Terminal` into `Dmon.Core` behind the RPC layer; the engine enumerates Core's DI-registered `IProviderFactory` instances to build the provider-selection step. **BREAKING** (internal: terminal wizard execution path).
- Re-home the serializable `WizardStep` type hierarchy (`WizardStep` base, `ChooseOneStep`, `ChooseManyStep`, `TextInputStep`, `YesNoStep`, `InfoStep`, `WizardCompletedStep`, `WizardOption`) from `Dmon.Abstractions` into `Dmon.Protocol`, and add a `Dmon.Abstractions → Dmon.Protocol` project reference. `WizardStep` becomes a polymorphic wire type — the factory's `GetNextStepAsync` output **is** the RPC payload, with no DTO mirroring. **BREAKING** (namespace move of public wizard types).
- `WizardState` **stays** in `Dmon.Abstractions` as pure in-process session state (never crosses the wire; Core holds it across round-trips).
- Add three new RPC carrier messages: `WizardStartCommand`, `WizardStepEvent` (embeds the polymorphic `WizardStep`), `WizardAnswerCommand` (carries an `Answered | Back | Cancel` outcome). Completion reuses the existing `ProviderConfiguredEvent`.
- Make the core's command dispatch **non-blocking for long-running interactive commands** (`turn.submit`, `wizard.start`): run them on a tracked background task so the serial stdin reader keeps pumping and can route the response/answer commands that resolve a suspended turn or wizard. This conforms the implementation to ADR-003's existing "commands are fire-and-forget; the core suspends the turn until the response arrives" framing, and fixes a latent deadlock that affects the existing tool-confirm / ui-input round-trips. **BREAKING** (internal dispatch concurrency).
- Delete the hard-coded factory list in `Program.cs` and **remove the `Dmon.Terminal → Dmon.Providers` project reference**. The Terminal constructs zero factories.
- Establish the invariant that `Dmon.Protocol` remains reference-free and `Dmon.Abstractions` may reference it.

## Capabilities

### New Capabilities
<!-- none — this change modifies existing capabilities only -->

### Modified Capabilities
- `provider-setup-wizard`: the wizard engine relocates from the terminal to `Dmon.Core`; provider selection enumerates Core's registered factories; the engine is driven over a new wizard RPC contract (start/step/answer + reuse of `ProviderConfiguredEvent`); the renderer maps `WizardStep` (now sourced from `Dmon.Protocol`) to terminal prompts, translating back/cancel into the answer command's outcome; persistence happens in Core at completion.
- `provider-factories`: the `WizardStep` base class and its subtypes are defined in `Dmon.Protocol` rather than `Dmon.Abstractions`; factories still implement `GetNextStepAsync` (substance unchanged), but its return type now resolves from the protocol assembly. Adds the leaf-dependency invariant (`Dmon.Protocol` reference-free; `Dmon.Abstractions` may reference it).
- `agent-core`: the command dispatch loop SHALL NOT block the stdin reader on long-running interactive commands; `turn.submit` and `wizard.start` run on background tasks so response/answer commands continue to be read and routed while a turn or wizard is suspended (conforms to ADR-003 fire-and-forget framing).

## Impact

- **Assemblies / references**: `Dmon.Abstractions.csproj` gains a `Dmon.Protocol` reference; `Dmon.Terminal.csproj` loses its `Dmon.Providers` reference; `Dmon.Providers`/`Dmon.Providers.Ollama` pick up `Dmon.Protocol` transitively (a pure leaf — harmless).
- **Code moves**: `src/Dmon.Abstractions/Wizard/*` → `src/Dmon.Protocol/Wizard/*`; `src/Dmon.Terminal/WizardEngine.cs` → a new `ProviderSetupHandler` in `src/Dmon.Core/Rpc/`.
- **RPC surface (ADR-003)**: three new message types registered on `Command`/`Event` polymorphic roots, plus `WizardStep` as a third polymorphic serialization root. Must be documented in the specs per the "no new RPC types without a spec" rule.
- **Dispatch concurrency**: `src/Dmon.Core/Rpc/CommandDispatcher.cs` (and possibly `RpcHostedService.cs`) reworked so long-running commands are dispatched on a tracked background task; a clarifying "Dispatch concurrency" subsection is added to ADR-003. An integration test drives a request→response round-trip through the real read loop (none exists today).
- **Terminal**: `Program.cs` factory list deleted; `ConsoleEventHandler.HandleAddProviderAsync` reworked to send `WizardStartCommand`, render `WizardStepEvent`, and reply with `WizardAnswerCommand`.
- **Factory implementations**: `using` directives updated across `Dmon.Providers/*Factory.cs` and `Dmon.Providers.Ollama/OllamaProviderFactory.cs` for the `WizardStep` namespace change.
- **ADRs**: reinforces ADR-003 (process boundary); serves ADR-007 (provider-extension lifecycle) and ADR-008 (extension load context). No accepted ADR is contradicted.
- **Out of scope**: the model-switch flow (`model.models` RPC) is already correct and untouched; no provider extension is authored; no new provider SDKs.
