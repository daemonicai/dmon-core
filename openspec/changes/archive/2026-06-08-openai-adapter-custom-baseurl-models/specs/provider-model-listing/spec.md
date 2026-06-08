## MODIFIED Requirements

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
