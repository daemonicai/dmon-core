## 1. Wizard abstraction in Dmon.Abstractions

- [x] 1.1 Add `WizardOption` (record: `Label`, `Value`, `string? Description`) under `Dmon.Abstractions/Wizard/`.
- [x] 1.2 Add abstract `WizardStep` base class: `init`-only `Id` and `Prompt`, read-only `IsAnswered` (settable by subclasses when answered).
- [x] 1.3 Add `ChooseOneStep` (`IReadOnlyList<WizardOption> Options`, `int? SelectedIndex` answer that flips `IsAnswered`).
- [x] 1.4 Add `ChooseManyStep` (`Options`, `int MinSelections`, `IReadOnlyList<int> SelectedIndices` answer).
- [x] 1.5 Add `TextInputStep` (`string? Default`, `bool Secret`, `bool Required`, `string? Value` answer).
- [x] 1.6 Add `YesNoStep` (`bool Default`, `bool? Answer`).
- [x] 1.7 Add `InfoStep` (no answer; advances on render).
- [x] 1.8 Add `WizardCompletedStep` (`string Message`, no answer).
- [x] 1.9 Add `WizardState` (record: immutable `IReadOnlyList<WizardStep> Steps`).
- [x] 1.10 Add `string DisplayName` and `ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)` to `IProviderFactory` (no default implementations). Confirm `DefaultEnvVar` is retained.

## 2. Migrate built-in factories to GetNextStepAsync

- [x] 2.1 Implement `DisplayName` and `GetNextStepAsync` in `AnthropicProviderFactory` (api-key step → model step from `GetAvailableModelsAsync` → build `ProviderConfig` + `WizardCompletedStep`).
- [x] 2.2 Implement `DisplayName` and `GetNextStepAsync` in `OpenAiProviderFactory` following the same pattern.
- [x] 2.3 Implement `DisplayName` and `GetNextStepAsync` in `GeminiProviderFactory` following the same pattern.
- [x] 2.4 Verify each factory reads prior answers by step id (`"api-key"`, `"model"`) and builds the in-flight `ProviderConfig` without persisting to disk.

## 3. Wizard engine and renderer in Dmon.Terminal

- [x] 3.1 Implement the wizard engine: build provider-selection `ChooseOneStep` from registered factories (`Label` = `DisplayName`, `Value` = `AdapterName`), resolve the factory, then loop on `GetNextStepAsync`.
- [x] 3.2 Implement the renderer: pattern-match `WizardStep` subtypes onto `InlinePrompt` calls; honour `Secret`/`Required` on `TextInputStep`; map back/cancel signals to engine outcomes.
- [x] 3.3 Implement loop semantics: append answered step on `Answered`, truncate last step and re-ask on `Back` (back from first factory step returns to provider selection), abandon on `Cancel`.
- [x] 3.4 On `WizardCompletedStep`, persist the provider config to global scope and render the completion `Message`.
- [x] 3.5 Delete the hardcoded adapter list, the env-var dictionary, and the local/global scope prompt from `WizardSteps.cs`; adapt or remove `WizardRunner.cs`/`WizardState.cs` as superseded by the engine.
- [x] 3.6 Confirm the completed wizard still emits the existing `ProviderConfigureCommand` to the core.

## 4. Build, test, and verify

- [x] 4.1 `dotnet build` the solution with no warnings (`TreatWarningsAsErrors`).
- [x] 4.2 Add a `test/Dmon.Terminal.Tests` project for engine tests; reuse `test/Dmon.Providers.Tests` for factory tests; register both in `Dmon.slnx`.
- [x] 4.3 Add unit tests for `GetNextStepAsync` flow per factory (first step is api-key, second is model list, third completes).
- [x] 4.4 Add unit tests for engine back/cancel navigation and global-scope persistence.
- [x] 4.5 Manually run `/add-provider` for Anthropic, OpenAI, and Gemini and confirm each completes and writes global config.
