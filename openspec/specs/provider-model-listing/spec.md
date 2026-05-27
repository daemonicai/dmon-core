## ADDED Requirements

### Requirement: IProviderFactory exposes async model listing
`IProviderFactory` SHALL declare `ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string? apiKey, CancellationToken cancellationToken = default)`. A default implementation SHALL be provided that returns an empty list, so existing external implementations do not require immediate changes.

#### Scenario: Method exists on interface
- **WHEN** any type implementing `IProviderFactory` is inspected
- **THEN** it has a `GetAvailableModelsAsync` member matching the signature above

#### Scenario: Default implementation returns non-empty list
- **WHEN** `GetAvailableModelsAsync` is called on an implementor that uses the default
- **THEN** the returned list is non-null and contains at least one `ModelInfo`

### Requirement: GeminiProviderFactory fetches models from the Gemini REST API
`GeminiProviderFactory.GetAvailableModelsAsync` SHALL call `GET https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}`, parse the `models` array, and return a `ModelInfo` per entry whose `name` starts with `models/gemini`. On any failure (network error, non-2xx response, JSON parse error, null/empty key) it SHALL return the static fallback list.

#### Scenario: Live fetch returns Gemini models
- **WHEN** a valid API key is supplied and the endpoint is reachable
- **THEN** the returned list contains `ModelInfo` entries with IDs such as `gemini-2.5-pro` (the `models/` prefix is stripped)

#### Scenario: Network failure falls back to static list
- **WHEN** the HTTP call throws or times out
- **THEN** the returned list equals the static fallback list and no exception propagates

#### Scenario: Null or empty key falls back to static list
- **WHEN** `apiKey` is null or whitespace
- **THEN** no HTTP call is made and the static fallback list is returned immediately

### Requirement: AnthropicProviderFactory fetches models from the Anthropic REST API
`AnthropicProviderFactory.GetAvailableModelsAsync` SHALL call `GET https://api.anthropic.com/v1/models` with headers `x-api-key: {apiKey}` and `anthropic-version: 2023-06-01`. On any failure it SHALL return the static fallback list.

#### Scenario: Live fetch returns Anthropic models
- **WHEN** a valid API key is supplied and the endpoint is reachable
- **THEN** the returned list contains `ModelInfo` entries with IDs matching the `data[].id` field from the response

#### Scenario: Non-2xx response falls back to static list
- **WHEN** the endpoint returns a 4xx or 5xx status
- **THEN** the returned list equals the static fallback list and no exception propagates

### Requirement: OpenAiProviderFactory fetches models from the OpenAI REST API
`OpenAiProviderFactory.GetAvailableModelsAsync` SHALL call `GET https://api.openai.com/v1/models` with header `Authorization: Bearer {apiKey}`, filter the `data` array to entries whose `id` starts with `gpt-` or `o` followed by a digit (covering `o1`, `o3`, `o4-mini`, etc.), and return those as `ModelInfo`. On any failure it SHALL return the static fallback list.

#### Scenario: Live fetch returns filtered OpenAI models
- **WHEN** a valid API key is supplied and the endpoint is reachable
- **THEN** the returned list contains only entries whose IDs start with `gpt-` or `o`+digit; embeddings, TTS, and other non-chat models are excluded

#### Scenario: Empty key falls back to static list
- **WHEN** `apiKey` is null or whitespace
- **THEN** no HTTP call is made and the static fallback list is returned immediately

### Requirement: GetAvailableModelsAsync uses a short timeout
Each factory's HTTP call SHALL complete within 5 seconds. If the server does not respond within that window the call is cancelled and the static fallback list is returned.

#### Scenario: Slow server triggers timeout fallback
- **WHEN** the model-listing endpoint does not respond within 5 seconds
- **THEN** `GetAvailableModelsAsync` returns the static fallback list without throwing

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
