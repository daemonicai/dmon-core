## MODIFIED Requirements

### Requirement: Inline wizard prompts

The terminal host SHALL present provider setup and `/add-provider` wizard steps via `dcli`'s awaitable dialog methods (`await ITerminal.SelectAsync` for list pickers, `await ITerminal.InputAsync` for free-text and secret inputs). The wizard step order SHALL be: (1) Select Adapter, (2) Auth Configuration, (3) Select Model. The model selection step SHALL call `IProviderFactory.GetAvailableModelsAsync` with the API key resolved from the env var entered in step 2. If the live fetch fails or returns an empty list, the step SHALL fall back to the factory's static model list. A brief `Fetching models…` status SHALL be shown via `Status.SetRows` while the fetch is in progress. Multi-step pickers (adapter, model) SHALL be opened with `SelectRequest { AllowBack = true }` so the user can navigate back via Backspace. Free-text and secret steps (auth configuration) SHALL be opened with `InputRequest { AllowBack = true }` so the user can navigate back via Backspace pressed while the input field is empty.

#### Scenario: Adapter selection prompt

- **WHEN** the wizard is at the adapter-selection step (step 1)
- **THEN** the host shows a `dcli` `SelectAsync` overlay listing available adapters with arrow-key navigation and Enter to select

#### Scenario: Auth config precedes model selection

- **WHEN** the user completes step 1 (adapter)
- **THEN** the wizard shows the auth configuration `InputAsync` prompt (step 2) before the model selection `SelectAsync` prompt (step 3)

#### Scenario: Live model list shown when key resolves

- **WHEN** the user completes step 2 and the env var they entered is set in the environment
- **THEN** step 3 shows the live model list fetched from the provider, with `Fetching models…` displayed via `Status.SetRows` while the call is in flight

#### Scenario: Static fallback shown when fetch fails

- **WHEN** the live model fetch in step 3 fails for any reason
- **THEN** the wizard shows the static fallback model list and continues normally without surfacing an error

#### Scenario: Back navigation via Backspace

- **WHEN** the user presses Backspace at a selection wizard step opened with `AllowBack = true` and before moving the selection
- **THEN** the dialog returns `DialogOutcome.Back` and the wizard returns to the previous step (using the existing back-stack)

#### Scenario: Back navigation from a text-input step via Backspace on empty

- **WHEN** the user presses Backspace at a free-text or secret wizard step opened with `InputRequest { AllowBack = true }` while the input field is empty
- **THEN** the dialog returns `DialogOutcome.Back` and the wizard returns to the previous step (using the existing back-stack)

#### Scenario: Wizard cancellation via Escape

- **WHEN** the user presses Escape during a wizard step
- **THEN** the dialog returns `DialogOutcome.Cancelled`, the wizard is cancelled, a notice is shown, and the input prompt is restored
