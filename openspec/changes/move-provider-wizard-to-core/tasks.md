## 1. Re-home wizard types into Dmon.Protocol

- [x] 1.1 Move `WizardStep`, `ChooseOneStep`, `ChooseManyStep`, `TextInputStep`, `YesNoStep`, `InfoStep`, `WizardCompletedStep`, and `WizardOption` from `src/Dmon.Abstractions/Wizard/` to `src/Dmon.Protocol/Wizard/`, updating their namespace to the protocol assembly
- [x] 1.2 Make `WizardStep` a `[JsonPolymorphic]` serialisation root with a `[JsonDerivedType]` discriminator per subtype (e.g. `wizard.chooseOne`, `wizard.chooseMany`, `wizard.textInput`, `wizard.yesNo`, `wizard.info`, `wizard.completed`)
- [x] 1.3 Mark each step's settable answer field `[JsonIgnore]` so only the question portion serialises; keep `IsAnswered` flipping behaviour intact
- [x] 1.4 Keep `WizardState` in `src/Dmon.Abstractions/Wizard/`; update its `using` to reference `WizardStep` from `Dmon.Protocol`
- [x] 1.5 Add the `Dmon.Abstractions → Dmon.Protocol` `ProjectReference`; confirm `Dmon.Protocol.csproj` still has zero references (no cycle)
- [x] 1.6 Update `using` directives for the namespace move in all consumers: `Dmon.Providers/AnthropicProviderFactory.cs`, `OpenAiProviderFactory.cs`, `GeminiProviderFactory.cs`, `Dmon.Providers.Ollama/OllamaProviderFactory.cs`, and `IProviderFactory`
- [x] 1.7 Build the solution; resolve all compile errors from the move. Add/adjust unit tests asserting a `ChooseOneStep` and `TextInputStep` round-trip polymorphically and that the answer field is omitted from serialised JSON

## 2. Wizard RPC carrier messages

- [x] 2.1 Add `WizardStartCommand { id }` in `src/Dmon.Protocol/Commands/` and register `[JsonDerivedType]` `wizard.start` on `Command`
- [x] 2.2 Add `WizardAnswerCommand { id, wizardId, outcome (Answered|Back|Cancel), value }` and register `wizard.answer` on `Command`; add the `WizardAnswerOutcome` enum under `src/Dmon.Protocol/Enums/`
- [x] 2.3 Add `WizardStepEvent { wizardId, WizardStep step }` in `src/Dmon.Protocol/Events/` and register `wizard.step` on `Event`
- [x] 2.4 Add serialisation tests for the three carriers, including a `WizardStepEvent` whose embedded `step` deserialises back to its concrete subtype

## 3. Non-blocking dispatch + wizard engine in Dmon.Core

- [x] 3.1 Extend the **existing** `ProviderSetupHandler` (`src/Dmon.Core/Rpc/`, currently handles only `ProviderConfigureCommand`) to also hold the active `WizardSession` (`WizardState` + resolved factory) keyed by the start command id, enforcing at most one active wizard; add the corresponding members to `IProviderSetupHandler`
- [x] 3.2 Port the engine loop from `WizardEngine`: build provider-selection (step 0) from Core's DI-registered `IProviderFactory` instances (via `IProviderRegistry` or injected factories); loop `GetNextStepAsync` emitting `WizardStepEvent` and awaiting `WizardAnswerCommand` via a `TaskCompletionSource` (mirror `TurnHandler._pendingUiInputs`)
- [x] 3.3 Implement `Answered` (apply + append to `WizardState`), `Back` (truncate last answered step, re-invoke), and `Cancel` (discard session, persist nothing) outcomes; ignore answers whose `wizardId` does not match the active session
- [x] 3.4 On `WizardCompletedStep`, persist by reusing the handler's existing `ConfigureAsync` persistence path (YAML stanza + `AddDynamicProvider` + `ProviderConfiguredEvent`) with scope `global`; do not duplicate the persistence logic
- [x] 3.5 Route `WizardStartCommand` and `WizardAnswerCommand` to `ProviderSetupHandler` in `CommandDispatcher` (alongside the existing `ProviderConfigureCommand` route)
- [x] 3.6 Confirm `ProviderSetupHandler` DI registration covers the new dependencies (it is already registered for `ProviderConfigureCommand`)
- [x] 3.7 Add tests: full happy-path flow (start → api-key → model → completed → `ProviderConfiguredEvent`), back navigation re-asks the prior step, cancel persists nothing, stale `wizardId` is ignored, and an extension-registered factory appears in provider selection
- [x] 3.8 **Substrate fix (D7):** make `CommandDispatcher`/`RpcHostedService` dispatch long-running interactive commands (`turn.submit`, `wizard.start`) on a tracked background task so the stdin reader keeps pumping and can route `tool.confirmResponse`/`ui.inputResponse`/`wizard.answer`/`turn.abort` to resolve a suspended operation; short commands stay inline; background-task errors surface as `error` events and tasks are observable at shutdown
- [x] 3.9 Add an integration test that drives a request→response round-trip through the **real** read loop (no out-of-band injection): a `turn.submit` that triggers a `tool.confirmRequest` answered by a subsequent `tool.confirmResponse`, and a `wizard.start` → `wizard.step` → `wizard.answer` flow — asserting completion rather than hang
- [x] 3.10 Fix reviewer N2: a malformed/empty/out-of-range `ChooseMany` answer re-prompts the current step (or emits `error` with `Recoverable = true`) instead of unwinding the wizard with `Recoverable = false`
- [x] 3.11 Fix reviewer N3: an invalid/unparseable `ChooseOne` answer (incl. provider-selection) re-prompts instead of appending an unanswered step and dereferencing a null `SelectedIndex`
- [x] 3.12 Fix reviewer N1: remove the dead 2-parameter `ProviderSetupHandler` constructor and migrate the two existing test call sites to the real 3-arg constructor (`[]` for factories)

## 4. Terminal rewiring

- [ ] 4.1 Rework `ConsoleEventHandler.HandleAddProviderAsync` to send `WizardStartCommand`, render each incoming `WizardStepEvent` via the renderer, and reply with `WizardAnswerCommand` (mapping answer/back/cancel to the outcome); treat `ProviderConfiguredEvent` as completion
- [ ] 4.2 Update the terminal renderer to pattern-match `WizardStep` subtypes (from `Dmon.Protocol`) onto `InlinePrompt` interactions, preserving secret masking and back/cancel translation per the spec
- [ ] 4.3 Delete `src/Dmon.Terminal/WizardEngine.cs` and the hard-coded factory list in `Program.cs` (lines ~47–53); remove the `providerFactories` wiring into `ConsoleEventHandler`
- [ ] 4.4 Remove the `Dmon.Terminal → Dmon.Providers` `ProjectReference` from `Dmon.Terminal.csproj`; confirm `Dmon.Providers.Ollama` is also unreferenced if it was only there for the factory list
- [ ] 4.5 Build the whole solution; confirm `Dmon.Terminal` compiles with no reference to `Dmon.Providers`

## 5. Validation and gates

- [ ] 5.1 `make build` clean (no warnings; `TreatWarningsAsErrors`)
- [ ] 5.2 `make test` green (new wizard-flow tests plus all existing tests)
- [ ] 5.3 `openspec validate move-provider-wizard-to-core --strict` passes
- [ ] 5.4 Manual smoke (HITL): run the terminal host, trigger add-provider, complete the wizard for one provider, confirm config persists and the agent becomes ready — provide a copy-pasteable recipe and await confirmation
