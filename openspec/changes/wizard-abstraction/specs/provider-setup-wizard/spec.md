## ADDED Requirements

### Requirement: WizardStep type hierarchy
The system SHALL define an abstract `WizardStep` base class in `Dmon.Abstractions` with an `init`-only `string Id`, an `init`-only `string Prompt`, and a read-only `bool IsAnswered`. The question portion of a step SHALL be immutable; the answer portion SHALL be settable and SHALL flip `IsAnswered` to `true` when set. The following concrete subtypes SHALL exist: `ChooseOneStep` (`IReadOnlyList<WizardOption> Options`, nullable `SelectedIndex` answer), `ChooseManyStep` (`Options`, `int MinSelections`, `IReadOnlyList<int> SelectedIndices` answer), `TextInputStep` (`string? Default`, `bool Secret`, `bool Required`, `string? Value` answer), `YesNoStep` (`bool Default`, `bool? Answer`), `InfoStep` (no answer; rendering advances), and `WizardCompletedStep` (carries a `string Message`, no answer). A `WizardOption` SHALL carry a display `Label`, a stored `Value`, and an optional `Description`.

#### Scenario: Answering a step flips IsAnswered
- **WHEN** a `TextInputStep` with `IsAnswered = false` has its `Value` set to a non-null string
- **THEN** `IsAnswered` becomes `true`

#### Scenario: Option separates display from stored value
- **WHEN** a `ChooseOneStep` presents a `WizardOption` with `Label = "Anthropic"` and `Value = "anthropic"`
- **THEN** the UI displays `"Anthropic"` while the resolved answer value is `"anthropic"`

#### Scenario: Completed step carries a message and no answer
- **WHEN** a factory returns a `WizardCompletedStep`
- **THEN** the step exposes a non-empty `Message` and has no answerable field

### Requirement: WizardState accumulates answered steps
The system SHALL define `WizardState` in `Dmon.Abstractions` holding an ordered, immutable `IReadOnlyList<WizardStep> Steps`. Each entry SHALL be an answered step (the adapter-selection step at index 0, followed by factory-produced steps in the order they were answered). The factory SHALL read prior answers from `WizardState.Steps` by step id and/or type.

#### Scenario: Factory reads a prior answer by id
- **WHEN** a factory needs the entered API key during `GetNextStepAsync`
- **THEN** it locates the answered `TextInputStep` whose `Id == "api-key"` in `WizardState.Steps` and reads its `Value`

### Requirement: Wizard engine drives the factory state machine
The terminal SHALL provide a wizard engine that, after provider selection, repeatedly calls `IProviderFactory.GetNextStepAsync(state, cancellationToken)`. For each returned step that is not a `WizardCompletedStep`, the engine SHALL render the step, capture the answer onto the step, append the answered step to `WizardState.Steps`, and loop. When `GetNextStepAsync` returns a `WizardCompletedStep`, the engine SHALL stop the loop, persist the resulting provider config, and render the step's `Message`.

#### Scenario: Engine loops until completion
- **WHEN** a factory returns a sequence of answerable steps followed by a `WizardCompletedStep`
- **THEN** the engine renders each answerable step in turn and stops when the `WizardCompletedStep` is returned

#### Scenario: Conditional step appears based on prior answer
- **WHEN** a factory inspects `WizardState` and returns a step only when a prior answer meets a condition
- **THEN** the engine renders that step only in runs where the condition holds, with no special handling in the engine

### Requirement: Engine owns provider selection
The wizard engine SHALL present provider selection as the first step, built as a `ChooseOneStep` whose options are derived from the registered `IProviderFactory` instances (`Label` from `DisplayName`, `Value` from `AdapterName`). The engine SHALL resolve the selected factory from the answer before entering the `GetNextStepAsync` loop. The adapter-selection step SHALL be `WizardState.Steps[0]`; factories SHALL NOT be required to interpret it.

#### Scenario: Provider list is built from registered factories
- **WHEN** the wizard starts and three factories are registered
- **THEN** the first step is a `ChooseOneStep` offering one option per factory, with no hardcoded adapter list in the terminal

#### Scenario: Selected factory drives subsequent steps
- **WHEN** the user selects a provider
- **THEN** the engine resolves the matching `IProviderFactory` and obtains all subsequent steps from its `GetNextStepAsync`

### Requirement: Back and cancel navigation
The wizard engine SHALL support back navigation by truncating the most recently answered step from `WizardState.Steps` and re-invoking `GetNextStepAsync`, causing the previous step to be re-asked. Back navigation from the first factory step SHALL return to provider selection. The engine SHALL support cancellation, which abandons the wizard without persisting any configuration. No provider configuration SHALL be persisted to disk before a `WizardCompletedStep` is returned.

#### Scenario: Back re-asks the previous step
- **WHEN** the user chooses "back" while on a factory-produced step
- **THEN** the engine removes the last answered step from `WizardState.Steps` and re-invokes `GetNextStepAsync`, which returns the previous step

#### Scenario: Cancel abandons without persisting
- **WHEN** the user cancels at any point before completion
- **THEN** the wizard exits and no provider configuration is written

#### Scenario: No disk writes before completion
- **WHEN** the user navigates forward and back through steps without reaching completion
- **THEN** no provider configuration is persisted, so there is nothing to unwind

### Requirement: Setup persists to global scope only
The wizard engine SHALL persist the provider configuration produced by the factory to the user-global config scope. The wizard SHALL NOT present a local/global scope choice. The factory SHALL build the in-flight `ProviderConfig` while collecting answers; the engine SHALL perform the persistence.

#### Scenario: Completed setup writes global config
- **WHEN** the wizard reaches a `WizardCompletedStep`
- **THEN** the engine persists the provider configuration to the user-global config scope

#### Scenario: No scope prompt is shown
- **WHEN** the user runs the provider setup wizard
- **THEN** no step asking to choose between local and global scope is presented

### Requirement: Renderer maps step types to terminal prompts
The terminal renderer SHALL pattern-match `WizardStep` subtypes onto `InlinePrompt` interactions: `ChooseOneStep`/`ChooseManyStep` to a selection prompt, `TextInputStep` to a line read (honouring `Secret` and `Required`), `YesNoStep` to a confirmation prompt, and `InfoStep` to a message that advances without input. The renderer SHALL translate the underlying back signal and cancel signal into the engine's back and cancel outcomes respectively.

#### Scenario: Secret text input is masked
- **WHEN** the renderer presents a `TextInputStep` with `Secret = true`
- **THEN** the input is masked as it is typed

#### Scenario: Selection back signal becomes a back outcome
- **WHEN** the user triggers "back" at a selection prompt
- **THEN** the renderer reports a back outcome to the engine rather than an answer
