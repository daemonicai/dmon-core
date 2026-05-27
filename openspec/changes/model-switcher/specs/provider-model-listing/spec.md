## ADDED Requirements

### Requirement: ModelModelsCommand requests live model list for a named provider
The protocol SHALL include a `ModelModelsCommand` with a required `provider` string property. The core SHALL route this command to `ModelModelsHandler`.

#### Scenario: Command carries provider name
- **WHEN** `ModelModelsCommand { Provider = "anthropic" }` is sent
- **THEN** the core routes it to `ModelModelsHandler` which resolves credentials for the `"anthropic"` provider

### Requirement: ModelModelsResultEvent carries live model list
The protocol SHALL include a `ModelModelsResultEvent` with properties `Provider` (string), `Models` (IReadOnlyList<string>), and `ActiveModelId` (string?). This event is emitted in response to `ModelModelsCommand`.

#### Scenario: Event carries fetched model IDs
- **WHEN** `ModelModelsCommand { Provider = "anthropic" }` is handled and the live fetch succeeds
- **THEN** `ModelModelsResultEvent` is emitted with `Provider = "anthropic"` and `Models` containing the fetched model ID strings

#### Scenario: Event carries ActiveModelId from registry
- **WHEN** the registry's committed model ID is `"claude-sonnet-4-6"` and `ModelModelsCommand { Provider = "anthropic" }` is handled
- **THEN** `ModelModelsResultEvent.ActiveModelId` equals `"claude-sonnet-4-6"`

#### Scenario: Fetch failure returns empty Models list
- **WHEN** the live model fetch throws or times out
- **THEN** `ModelModelsResultEvent` is emitted with `Models = []` (empty list, falling back to factory default via `GetAvailableModelsAsync`)

### Requirement: ModelListResultEvent.ActiveModelId reflects runtime registry state
`ModelListHandler` SHALL source `ActiveModelId` from `IProviderRegistry.GetCurrentModelId()`, falling back to the active provider's `DefaultModelId` if `GetCurrentModelId()` returns null.

#### Scenario: ActiveModelId reflects committed switch
- **WHEN** the user has previously switched to `"gpt-4o"` and `ModelListCommand` is handled
- **THEN** `ModelListResultEvent.ActiveModelId` equals `"gpt-4o"`

#### Scenario: ActiveModelId falls back to DefaultModelId before first switch
- **WHEN** no model switch has been committed and `ModelListCommand` is handled
- **THEN** `ModelListResultEvent.ActiveModelId` equals the active provider's `DefaultModelId`

### Requirement: ModelModelsHandler uses a 5-second timeout
`ModelModelsHandler` SHALL apply a 5-second timeout to the `GetAvailableModelsAsync` call. On timeout it SHALL return a `ModelModelsResultEvent` with an empty `Models` list (factory fallback behaviour applies).

#### Scenario: Timeout returns empty list
- **WHEN** the provider's model endpoint does not respond within 5 seconds
- **THEN** `ModelModelsResultEvent.Models` is empty and no exception propagates to the caller
