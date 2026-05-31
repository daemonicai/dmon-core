## MODIFIED Requirements

### Requirement: Renderer maps step types to terminal prompts
The terminal renderer SHALL pattern-match `WizardStep` subtypes received via `WizardStepEvent` (sourced from `Dmon.Protocol`) onto `InlinePrompt` interactions: `ChooseOneStep` to a single-select prompt (`SelectAsync`), `ChooseManyStep` to a multi-select prompt (`MultiSelectAsync`) that returns the set of checked indices, `TextInputStep` to a line read (honouring `Secret` and `Required`), `YesNoStep` to a confirmation prompt, and `InfoStep` to a message that advances without input. For a `ChooseManyStep`, the renderer SHALL encode the answer as the checked zero-based indices joined as a comma-separated string (matching the wire format decoded by `WizardAnswerHelper.DecodeChooseManyIndices`). The renderer SHALL translate the underlying back signal and cancel signal into a `WizardAnswerCommand` with outcome `Back` and `Cancel` respectively, and an answered step into a `WizardAnswerCommand` with outcome `Answered`. When a `TextInputStep` has `Secret = true` and a non-null `Default`, the renderer SHALL display a masked hint (`********`) rather than the raw value.

#### Scenario: Secret text input is masked
- **WHEN** the renderer presents a `TextInputStep` with `Secret = true`
- **THEN** the input is masked as it is typed

#### Scenario: Secret default hint is masked
- **WHEN** the renderer shows a default hint for a `TextInputStep` with `Secret = true`
- **THEN** the hint displays `********` rather than the actual default value

#### Scenario: Selection back signal becomes a back outcome
- **WHEN** the user triggers "back" at a selection prompt
- **THEN** the renderer sends a `WizardAnswerCommand` with outcome `Back` rather than an answer

#### Scenario: Choose-many renders as a multi-select prompt
- **WHEN** the renderer presents a `ChooseManyStep`
- **THEN** it shows a `MultiSelectAsync` overlay where Space toggles individual items and Enter submits the set of checked items

#### Scenario: Choose-many answer carries multiple indices
- **WHEN** the user submits a `ChooseManyStep` with more than one item checked
- **THEN** the renderer sends a `WizardAnswerCommand` with outcome `Answered` whose value is the checked zero-based indices joined as a comma-separated string

#### Scenario: Choose-many back signal becomes a back outcome
- **WHEN** the user triggers "back" at a `ChooseManyStep` multi-select prompt (the `[` key, with `AllowBack = true`)
- **THEN** the renderer sends a `WizardAnswerCommand` with outcome `Back` rather than an answer
