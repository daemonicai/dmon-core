## Requirements

### Requirement: /model command triggers interactive two-step picker
The terminal SHALL recognise `/model` (entered alone on a line) as a command that sends `ModelListCommand` to the core, then presents a two-step interactive picker: first a provider list, then a live-fetched model list for the selected provider. On completion it sends `ModelSetCommand { Provider, ModelId }`. Escape at any point cancels without sending any command.

#### Scenario: /model sends ModelListCommand
- **WHEN** the user types `/model` and presses Enter
- **THEN** the terminal sends `ModelListCommand` to the core and locks user input

#### Scenario: Provider picker shown on ModelListResultEvent
- **WHEN** `ModelListResultEvent` arrives with a non-empty provider list
- **THEN** the terminal displays an arrow-key provider picker, with the provider whose name matches `ActiveProvider` pre-selected

#### Scenario: Provider selection triggers model fetch
- **WHEN** the user confirms a provider selection in the provider picker
- **THEN** the terminal sends `ModelModelsCommand { Provider }` to the core and shows a loading indicator

#### Scenario: Model picker shown on ModelModelsResultEvent
- **WHEN** `ModelModelsResultEvent` arrives for the selected provider
- **THEN** the terminal replaces the loading indicator with an arrow-key model picker; if the selected provider matches the currently active provider, the model matching `ActiveModelId` is pre-selected; otherwise index 0 is selected

#### Scenario: Model selection sends ModelSetCommand
- **WHEN** the user confirms a model selection in the model picker
- **THEN** the terminal sends `ModelSetCommand { Provider, ModelId }` and unlocks user input

#### Scenario: Escape during provider picker cancels
- **WHEN** the user presses Escape while the provider picker is displayed
- **THEN** the picker closes, no command is sent, and user input is unlocked

#### Scenario: Escape during model picker cancels
- **WHEN** the user presses Escape while the model picker is displayed
- **THEN** the picker closes, no command is sent, and user input is unlocked

### Requirement: Terminal shows loading state between provider selection and model list
Between the user confirming a provider and `ModelModelsResultEvent` arriving, the terminal SHALL display a loading indicator (e.g., spinner or text) to communicate that a live fetch is in progress.

#### Scenario: Loading state displayed
- **WHEN** `ModelModelsCommand` has been sent and `ModelModelsResultEvent` has not yet arrived
- **THEN** the terminal shows a loading indicator in the picker area

#### Scenario: Loading state replaced by picker
- **WHEN** `ModelModelsResultEvent` arrives
- **THEN** the loading indicator is replaced by the model picker list
