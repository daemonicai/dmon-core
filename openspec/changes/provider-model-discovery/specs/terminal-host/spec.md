## MODIFIED Requirements

### Requirement: Inline wizard prompts
The terminal host SHALL present provider setup and `/add-provider` wizard steps as numbered inline prompts rendered to stdout. The wizard step order SHALL be: (1) Select Adapter, (2) Auth Configuration, (3) Select Model. The model selection step SHALL call `IProviderFactory.GetAvailableModelsAsync` with the API key resolved from the env var entered in step 2. If the live fetch fails or returns an empty list, the step SHALL fall back to the factory's static model list. A brief `Fetching models…` status line SHALL be shown while the fetch is in progress.

#### Scenario: Adapter selection prompt
- **WHEN** the wizard is at the adapter-selection step (step 1)
- **THEN** the host prints a numbered list of available adapters and reads a single digit from the user

#### Scenario: Auth config precedes model selection
- **WHEN** the user completes step 1 (adapter)
- **THEN** the wizard shows the auth configuration prompt (step 2) before the model selection prompt (step 3)

#### Scenario: Live model list shown when key resolves
- **WHEN** the user completes step 2 and the env var they entered is set in the environment
- **THEN** step 3 shows the live model list fetched from the provider, with `Fetching models…` displayed while the call is in flight

#### Scenario: Static fallback shown when fetch fails
- **WHEN** the live model fetch in step 3 fails for any reason
- **THEN** the wizard shows the static fallback model list and continues normally without surfacing an error

#### Scenario: Back navigation
- **WHEN** the user types `b` or `0` at a wizard step
- **THEN** the wizard returns to the previous step (using `WizardRunner`'s back-stack)

#### Scenario: Wizard cancellation
- **WHEN** the user presses Ctrl+C during a wizard step
- **THEN** the wizard is cancelled, a notice is shown, and the input prompt is restored

## ADDED Requirements

### Requirement: WizardState carries a transient resolved API key
`Dmon.Terminal`'s `WizardState` SHALL include a `string? ResolvedApiKey` property. The auth configuration step (step 2) SHALL resolve the actual API key value from the environment variable name the user entered and store it in `ResolvedApiKey`. This field SHALL NOT be written to any config file or persistent store; it exists only in-memory for the duration of the wizard run.

#### Scenario: ResolvedApiKey set after auth step
- **WHEN** the user completes the auth configuration step with an env var name that is set in the environment
- **THEN** `WizardState.ResolvedApiKey` contains the value of that env var

#### Scenario: ResolvedApiKey null when env var not set
- **WHEN** the env var name the user entered is not set in the environment
- **THEN** `WizardState.ResolvedApiKey` is null and the model step falls back to the static list

#### Scenario: ResolvedApiKey not persisted
- **WHEN** the wizard completes and the provider config is written
- **THEN** the written config file contains no `ResolvedApiKey` field
