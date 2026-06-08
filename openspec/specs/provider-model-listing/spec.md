## Purpose

Define how each provider factory exposes a live model list via `GetAvailableModelsAsync`, how the `ModelModelsCommand`/`ModelModelsResultEvent` protocol pair surfaces those lists to the terminal host, and the timeout and fallback behaviour that keeps the picker responsive when a provider endpoint is slow or unreachable.
## Requirements
### Requirement: IProviderFactory exposes async model listing
`IProviderFactory` SHALL declare `ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string? apiKey, string? baseUrl = null, CancellationToken cancellationToken = default)`. The `baseUrl` parameter SHALL carry the provider's configured custom endpoint (or null when none is configured). A default implementation SHALL be provided that returns an empty list, so existing external implementations do not require immediate changes. `ModelModelsHandler` SHALL pass the resolved `ProviderConfig.BaseUrl` as `baseUrl`.

#### Scenario: Method exists on interface
- **WHEN** any type implementing `IProviderFactory` is inspected
- **THEN** it has a `GetAvailableModelsAsync` member matching the signature above, with `baseUrl` preceding `cancellationToken`

#### Scenario: Default implementation returns non-empty list
- **WHEN** `GetAvailableModelsAsync` is called on an implementor that uses the default
- **THEN** the returned list is non-null and contains at least one `ModelInfo`

#### Scenario: Handler forwards configured baseUrl
- **WHEN** `ModelModelsHandler` handles a `ModelModelsCommand` for a provider whose config has `BaseUrl = "http://localhost:8080/v1"`
- **THEN** it calls the factory's `GetAvailableModelsAsync` with `baseUrl = "http://localhost:8080/v1"`

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
`OpenAiProviderFactory.GetAvailableModelsAsync` SHALL select its listing endpoint from the `baseUrl` parameter:

- When `baseUrl` is null or whitespace (the real OpenAI endpoint), it SHALL call `GET https://api.openai.com/v1/models` with header `Authorization: Bearer {apiKey}`, filter the `data` array to entries whose `id` starts with `gpt-` or `o` followed by a digit (covering `o1`, `o3`, `o4-mini`, etc.), and return those as `ModelInfo`. On any failure, or when `apiKey` is null/whitespace, it SHALL return the static fallback list.
- When `baseUrl` is set (an OpenAI-compatible custom endpoint), it SHALL call `GET {baseUrl}/models` (the configured `baseUrl` already carries the API version segment; a trailing slash is trimmed before appending `/models`). It SHALL send the `Authorization: Bearer {apiKey}` header only when `apiKey` is non-empty. It SHALL return every `id` in the `data` array as `ModelInfo` **without** the `gpt-`/`o`+digit filter. On any failure, or when the `data` array is empty, it SHALL return an empty list and SHALL NOT return the static OpenAI fallback list.

#### Scenario: Live fetch from OpenAI returns filtered models
- **WHEN** `baseUrl` is null, a valid API key is supplied, and the endpoint is reachable
- **THEN** the returned list contains only entries whose IDs start with `gpt-` or `o`+digit; embeddings, TTS, and other non-chat models are excluded

#### Scenario: Empty key falls back to static list only for OpenAI endpoint
- **WHEN** `baseUrl` is null and `apiKey` is null or whitespace
- **THEN** no HTTP call is made and the static fallback list is returned immediately

#### Scenario: Custom baseUrl is queried for models
- **WHEN** `baseUrl` is `"http://localhost:8080/v1"` and the server responds with a `data` array containing `llama3.2` and `mlx-community/Meta-Llama-3.1-8B-Instruct-4bit`
- **THEN** `GET http://localhost:8080/v1/models` is issued and the returned list contains both IDs unfiltered

#### Scenario: Custom baseUrl with no key sends no Authorization header
- **WHEN** `baseUrl` is set and `apiKey` is null or whitespace
- **THEN** the request to `{baseUrl}/models` is issued without an `Authorization` header

#### Scenario: Custom baseUrl failure returns empty list, not OpenAI fallback
- **WHEN** `baseUrl` is set and the request throws, times out, returns a non-2xx status, or yields an empty `data` array
- **THEN** an empty list is returned and the static OpenAI fallback list (`gpt-4o`, `gpt-4o-mini`, `o3`) is never returned

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

### Requirement: ModelModelsHandler uses a 5-second timeout
`ModelModelsHandler` SHALL apply a 5-second timeout to the `GetAvailableModelsAsync` call. On timeout it SHALL return a `ModelModelsResultEvent` with an empty `Models` list (factory fallback behaviour applies).

#### Scenario: Timeout returns empty list
- **WHEN** the provider's model endpoint does not respond within 5 seconds
- **THEN** `ModelModelsResultEvent.Models` is empty and no exception propagates to the caller

