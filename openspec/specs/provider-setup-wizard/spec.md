## Purpose

This capability defines the interactive provider setup wizard: the `WizardStep` type hierarchy and accumulating state, the engine that drives each factory's step machine, back/cancel navigation, global-scope persistence, the wizard RPC contract, and terminal rendering of steps.
## Requirements
### Requirement: WizardStep type hierarchy
The system SHALL define an abstract `WizardStep` base class in `Dmon.Protocol` with an `init`-only `string Id`, an `init`-only `string Prompt`, and a read-only `bool IsAnswered`. The question portion of a step SHALL be immutable; the answer portion SHALL be settable and SHALL flip `IsAnswered` to `true` when set, and SHALL be marked `[JsonIgnore]` so that only the question portion serialises on the wire. `WizardStep` SHALL be a `[JsonPolymorphic]` serialisation root with a `[JsonDerivedType]` discriminator per subtype, alongside `Command` and `Event`. The following concrete subtypes SHALL exist: `ChooseOneStep` (`IReadOnlyList<WizardOption> Options`, nullable `SelectedIndex` answer), `ChooseManyStep` (`Options`, `int MinSelections`, `IReadOnlyList<int> SelectedIndices` answer), `TextInputStep` (`string? Default`, `bool Secret`, `bool Required`, `string? Value` answer), `YesNoStep` (`bool Default`, `bool? Answer`), `InfoStep` (no answer; rendering advances), and `WizardCompletedStep` (carries a `string Message`, no answer). A `WizardOption` SHALL be defined in `Dmon.Protocol` and SHALL carry a display `Label`, a stored `Value`, and an optional `Description`.

#### Scenario: Answering a step flips IsAnswered
- **WHEN** a `TextInputStep` with `IsAnswered = false` has its `Value` set to a non-null string
- **THEN** `IsAnswered` becomes `true`

#### Scenario: Option separates display from stored value
- **WHEN** a `ChooseOneStep` presents a `WizardOption` with `Label = "Anthropic"` and `Value = "anthropic"`
- **THEN** the UI displays `"Anthropic"` while the resolved answer value is `"anthropic"`

#### Scenario: Completed step carries a message and no answer
- **WHEN** a factory returns a `WizardCompletedStep`
- **THEN** the step exposes a non-empty `Message` and has no answerable field

#### Scenario: Step serialises to the wire without its answer
- **WHEN** a `ChooseOneStep` is serialised for transmission as part of a `WizardStepEvent`
- **THEN** the JSON carries the type discriminator, `Id`, `Prompt`, and `Options`, and omits the `[JsonIgnore]` answer field

#### Scenario: Step round-trips polymorphically
- **WHEN** a serialised `TextInputStep` is deserialised against the `WizardStep` base type
- **THEN** the concrete `TextInputStep` is reconstructed via its `[JsonDerivedType]` discriminator

### Requirement: WizardState accumulates answered steps
The system SHALL define `WizardState` in `Dmon.Abstractions` holding an ordered, immutable `IReadOnlyList<WizardStep> Steps`. Each entry SHALL be an answered step (the adapter-selection step at index 0, followed by factory-produced steps in the order they were answered). The factory SHALL read prior answers from `WizardState.Steps` by step id and/or type.

#### Scenario: Factory reads a prior answer by id
- **WHEN** a factory needs the entered API key during `GetNextStepAsync`
- **THEN** it locates the answered `TextInputStep` whose `Id == "api-key"` in `WizardState.Steps` and reads its `Value`

### Requirement: Wizard engine drives the factory state machine
`Dmon.Core` SHALL provide a wizard engine that, after provider selection, repeatedly calls `IProviderFactory.GetNextStepAsync(state, cancellationToken)`. For each returned step that is not a `WizardCompletedStep`, the engine SHALL emit the step to the host as a `WizardStepEvent`, await the host's `WizardAnswerCommand`, apply the answer to the corresponding step, append the answered step to `WizardState.Steps`, and loop. When `GetNextStepAsync` returns a `WizardCompletedStep`, the engine SHALL stop the loop, persist the resulting provider config, and emit the completion to the host (the `WizardCompletedStep.Message` is rendered terminal-side). The `WizardState` SHALL be held in `Dmon.Core` for the duration of the wizard and SHALL NOT cross the wire.

#### Scenario: Engine loops until completion
- **WHEN** a factory returns a sequence of answerable steps followed by a `WizardCompletedStep`
- **THEN** the engine emits a `WizardStepEvent` for each answerable step in turn, awaiting a `WizardAnswerCommand` between them, and stops when the `WizardCompletedStep` is returned

#### Scenario: Conditional step appears based on prior answer
- **WHEN** a factory inspects `WizardState` and returns a step only when a prior answer meets a condition
- **THEN** the engine emits that step only in runs where the condition holds, with no special handling in the engine

#### Scenario: Wizard state stays in Core
- **WHEN** a wizard is in progress across multiple step/answer round-trips
- **THEN** the accumulating `WizardState` is held in `Dmon.Core` and is never serialised to the host

### Requirement: Engine owns provider selection
The wizard engine SHALL present provider selection as the first step, built as a `ChooseOneStep` whose options are derived from the `IProviderFactory` instances registered in `Dmon.Core`'s dependency-injection container — including any factory loaded as an extension into Core's `AssemblyLoadContext` (`Label` from `DisplayName`, `Value` from `AdapterName`). The engine SHALL resolve the selected factory from the answer before entering the `GetNextStepAsync` loop. The adapter-selection step SHALL be `WizardState.Steps[0]`; factories SHALL NOT be required to interpret it.

#### Scenario: Provider list is built from Core's registered factories
- **WHEN** the wizard starts and three factories are registered in Core's DI container
- **THEN** the first step is a `ChooseOneStep` offering one option per factory, with no hardcoded adapter list in the terminal

#### Scenario: Extension-loaded provider appears in selection
- **WHEN** a provider factory is loaded only as an extension into Core's `AssemblyLoadContext` and registered in DI
- **THEN** it appears as an option in the provider-selection step without any change to the terminal

#### Scenario: Selected factory drives subsequent steps
- **WHEN** the user selects a provider
- **THEN** the engine resolves the matching `IProviderFactory` and obtains all subsequent steps from its `GetNextStepAsync`

### Requirement: Back and cancel navigation
The wizard engine SHALL support back navigation when the host sends a `WizardAnswerCommand` with outcome `Back`, by truncating the most recently answered step from `WizardState.Steps` and re-invoking `GetNextStepAsync`, causing the previous step to be re-asked. Back navigation from the first factory step SHALL return to provider selection. The engine SHALL support cancellation when the host sends a `WizardAnswerCommand` with outcome `Cancel`, which abandons the wizard without persisting any configuration and discards the in-Core `WizardState`. No provider configuration SHALL be persisted before a `WizardCompletedStep` is returned.

#### Scenario: Back re-asks the previous step
- **WHEN** the host sends a `WizardAnswerCommand` with outcome `Back` while on a factory-produced step
- **THEN** the engine removes the last answered step from `WizardState.Steps` and re-invokes `GetNextStepAsync`, which returns the previous step

#### Scenario: Cancel abandons without persisting
- **WHEN** the host sends a `WizardAnswerCommand` with outcome `Cancel` at any point before completion
- **THEN** the engine discards the `WizardState` and no provider configuration is written

#### Scenario: No disk writes before completion
- **WHEN** the host navigates forward and back through steps without reaching completion
- **THEN** no provider configuration is persisted, so there is nothing to unwind

### Requirement: Setup persists to global scope only
The wizard engine in `Dmon.Core` SHALL persist the provider configuration produced by the factory to the user-global config scope. The wizard SHALL NOT present a local/global scope choice. The factory SHALL build the in-flight `ProviderConfig` while collecting answers; the engine SHALL perform the persistence and SHALL emit a `ProviderConfiguredEvent` once persistence succeeds.

#### Scenario: Completed setup writes global config
- **WHEN** the wizard reaches a `WizardCompletedStep`
- **THEN** the engine persists the provider configuration to the user-global config scope and emits a `ProviderConfiguredEvent`

#### Scenario: No scope prompt is shown
- **WHEN** the user runs the provider setup wizard
- **THEN** no step asking to choose between local and global scope is presented

### Requirement: Renderer maps step types to terminal prompts
The terminal renderer SHALL pattern-match `WizardStep` subtypes received via `WizardStepEvent` (sourced from `Dmon.Protocol`) onto `InlinePrompt` interactions: `ChooseOneStep`/`ChooseManyStep` to a selection prompt, `TextInputStep` to a line read (honouring `Secret` and `Required`), `YesNoStep` to a confirmation prompt, and `InfoStep` to a message that advances without input. The renderer SHALL translate the underlying back signal and cancel signal into a `WizardAnswerCommand` with outcome `Back` and `Cancel` respectively, and an answered step into a `WizardAnswerCommand` with outcome `Answered`. When a `TextInputStep` has `Secret = true` and a non-null `Default`, the renderer SHALL display a masked hint (`********`) rather than the raw value.

#### Scenario: Secret text input is masked
- **WHEN** the renderer presents a `TextInputStep` with `Secret = true`
- **THEN** the input is masked as it is typed

#### Scenario: Secret default hint is masked
- **WHEN** the renderer shows a default hint for a `TextInputStep` with `Secret = true`
- **THEN** the hint displays `********` rather than the actual default value

#### Scenario: Selection back signal becomes a back outcome
- **WHEN** the user triggers "back" at a selection prompt
- **THEN** the renderer sends a `WizardAnswerCommand` with outcome `Back` rather than an answer

### Requirement: Wizard RPC contract
The provider setup wizard SHALL be driven over the RPC layer (ADR-003) using a dedicated message family. The host SHALL initiate the wizard with a `WizardStartCommand` (discriminator `wizard.start`). `Dmon.Core` SHALL emit each step as a `WizardStepEvent` (discriminator `wizard.step`) carrying a `wizardId` and the polymorphic `WizardStep`. The host SHALL reply with a `WizardAnswerCommand` (discriminator `wizard.answer`) carrying the `wizardId`, an `outcome` of `Answered`, `Back`, or `Cancel`, and the answer value when the outcome is `Answered`. Completion SHALL be signalled by reusing the existing `ProviderConfiguredEvent`; no new completion event SHALL be introduced. `Dmon.Core` SHALL hold at most one active wizard session, keyed by the `wizardId` from the start command, and SHALL ignore a `WizardAnswerCommand` whose `wizardId` does not match the active session.

#### Scenario: Host starts the wizard
- **WHEN** the host sends a `WizardStartCommand`
- **THEN** `Dmon.Core` creates a wizard session keyed by the command id and emits the first `WizardStepEvent` (provider selection)

#### Scenario: Answer advances the wizard
- **WHEN** the host replies with a `WizardAnswerCommand` of outcome `Answered` carrying the active `wizardId`
- **THEN** the engine applies the answer and emits the next `WizardStepEvent`, or a `ProviderConfiguredEvent` if the flow is complete

#### Scenario: Stale answer is ignored
- **WHEN** a `WizardAnswerCommand` arrives whose `wizardId` does not match the active session (e.g. after a cancel)
- **THEN** `Dmon.Core` ignores it and does not mutate any wizard state

#### Scenario: Completion reuses ProviderConfiguredEvent
- **WHEN** the engine persists configuration at a `WizardCompletedStep`
- **THEN** it emits the existing `ProviderConfiguredEvent` rather than a new wizard-specific completion event

