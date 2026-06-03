## MODIFIED Requirements

### Requirement: ModelModelsResultEvent carries live model list
The protocol SHALL include a `ModelModelsResultEvent` with properties `Id` (string, the originating command id, serialized as `id`), `Provider` (string), `Models` (IReadOnlyList<string>), and `ActiveModelId` (string?). `ModelModelsResultEvent` SHALL derive from the `ResultEvent` correlation base. This event is emitted in response to `ModelModelsCommand` and SHALL carry that command's `id`.

#### Scenario: Event carries originating command id
- **WHEN** `ModelModelsCommand { Id = "req-1", Provider = "anthropic" }` is handled
- **THEN** `ModelModelsResultEvent.Id` equals `"req-1"`

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
`ModelListHandler` SHALL source `ActiveModelId` from `IProviderRegistry.GetCurrentModelId()`, falling back to the active provider's `DefaultModelId` if `GetCurrentModelId()` returns null. `ModelListResultEvent` SHALL derive from the `ResultEvent` correlation base and carry the originating `ModelListCommand`'s `id` (serialized as `id`).

#### Scenario: Event carries originating command id
- **WHEN** `ModelListCommand { Id = "req-2" }` is handled
- **THEN** `ModelListResultEvent.Id` equals `"req-2"`

#### Scenario: ActiveModelId reflects committed switch
- **WHEN** the user has previously switched to `"gpt-4o"` and `ModelListCommand` is handled
- **THEN** `ModelListResultEvent.ActiveModelId` equals `"gpt-4o"`

#### Scenario: ActiveModelId falls back to DefaultModelId before first switch
- **WHEN** no model switch has been committed and `ModelListCommand` is handled
- **THEN** `ModelListResultEvent.ActiveModelId` equals the active provider's `DefaultModelId`
