## Why

The provider setup wizard currently lives entirely in `Dmon.Terminal`, with hardcoded knowledge that belongs to providers: a fixed adapter list (`["anthropic", "openai", "gemini"]`), a per-adapter env-var dictionary, and a fixed step order. Adding a provider means editing the terminal, and providers cannot express their own setup flow (optional auth, base-URL overrides, conditional steps). This blocks the upcoming oMLX and Ollama provider work, where setup is genuinely provider-specific.

## What Changes

- Add a `WizardStep` abstraction to `Dmon.Abstractions`: an abstract base class carrying an immutable question and a mutable answer, with concrete types `ChooseOneStep`, `TextInputStep`, `YesNoStep`, `InfoStep`, and the terminal marker `WizardCompletedStep`. Add `WizardOption` (label/value/description) and `WizardState` (the ordered list of answered steps).
- **BREAKING** (internal interface): add `ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)` and `string DisplayName` to `IProviderFactory`. The factory becomes a state machine: given the answered steps so far, it returns the next step to show, or a `WizardCompletedStep` (carrying a final user message) to signal completion. `DefaultEnvVar` is retained for non-wizard use (validation, help text).
- Build a wizard engine and renderer in `Dmon.Terminal`. The engine owns provider selection (the first step, from which it resolves the factory), drives the `GetNextStepAsync` loop, handles **back** (truncate `WizardState.Steps` and re-ask) and **cancel**, and persists the resulting `ProviderConfig` to **global scope only**. The renderer pattern-matches `WizardStep` subtypes onto existing `InlinePrompt` calls.
- The factory builds the in-flight `ProviderConfig` as it collects answers; the engine performs the global persistence.
- Migrate the three existing factories (Anthropic, OpenAI, Gemini) to `GetNextStepAsync`.
- **Remove** the hardcoded adapter list, the env-var dictionary, and the local/global scope prompt from `WizardSteps.cs`. Local-only providers become a documented power-user edit of `[PROJECT_DIR]/.dmon/config.yaml`, not surfaced in the UI.

## Capabilities

### New Capabilities

- `provider-setup-wizard`: The wizard abstraction and engine — `WizardStep` type hierarchy, `WizardState`, `WizardOption`, the state-machine contract driven by `IProviderFactory.GetNextStepAsync`, engine-owned provider selection, back/cancel navigation, and global-scope persistence of the resulting provider config.

### Modified Capabilities

- `provider-factories`: `IProviderFactory` gains `GetNextStepAsync(WizardState, CancellationToken)` and `DisplayName`. Existing members are unchanged; `DefaultEnvVar` is retained.

## Impact

- **`Dmon.Abstractions`**: new `Wizard/` types (`WizardStep` and subtypes, `WizardOption`, `WizardState`); `IProviderFactory` interface additions.
- **`Dmon.Providers`**: Anthropic, OpenAI, Gemini factories implement `GetNextStepAsync` and `DisplayName`.
- **`Dmon.Terminal`**: new wizard engine + renderer; deletion of hardcoded adapter/env-var/scope logic in `WizardSteps.cs`; `WizardRunner.cs` adapted to the state-machine loop.
- **No impact** on the RPC protocol (the existing `ProviderConfigureCommand` carries the result), session storage, permission model, or the agent core.
- **`Dmon.Tui`** is a dead branch and is explicitly out of scope.
- Out of scope (separate follow-on changes): migrating the oMLX provider, and adding the Ollama provider with its cloud + memory-aware steps.
