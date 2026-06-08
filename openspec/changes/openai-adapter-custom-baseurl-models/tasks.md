## 1. Widen the model-listing interface

- [ ] 1.1 Add the optional `string? baseUrl = null` parameter (before `cancellationToken`) to `IProviderFactory.GetAvailableModelsAsync` in `src/Dmon.Abstractions/Providers/IProviderFactory.cs`, keeping the default interface implementation that returns an empty list.
- [ ] 1.2 Update the in-tree implementors (`AnthropicProviderFactory`, `GeminiProviderFactory`, `OllamaProviderFactory`, `OpenAiProviderFactory`) to match the new signature; the non-OpenAI factories ignore `baseUrl` and retain their current behaviour.
- [ ] 1.3 Update internal call sites that invoke `GetAvailableModelsAsync` (the wizard `GetNextStepAsync` paths in `OpenAiProviderFactory`/`OllamaProviderFactory`) so they still compile — they pass no `baseUrl` (OpenAI cloud) or their existing connection value (Ollama).

## 2. Pass the configured baseUrl from the handler

- [ ] 2.1 In `src/Dmon.Core/Providers/ModelModelsHandler.cs`, pass `config.BaseUrl` as the `baseUrl` argument to `factory.GetAvailableModelsAsync`.

## 3. OpenAI factory: honour custom baseUrl

- [ ] 3.1 In `OpenAiProviderFactory.GetAvailableModelsAsync`, branch on `baseUrl`: when null/whitespace keep the existing OpenAI-cloud path (hardcoded host, `Authorization: Bearer {apiKey}`, `gpt-`/`o`+digit filter, static fallback on null key or failure).
- [ ] 3.2 When `baseUrl` is set, issue `GET {baseUrl.TrimEnd('/')}/models`, send `Authorization: Bearer {apiKey}` only when `apiKey` is non-empty, return every `data[].id` as `ModelInfo` unfiltered, and return an **empty** list (never the static fallback) on failure, non-2xx, timeout, or empty `data`.
- [ ] 3.3 Preserve the 5-second timeout for both branches.
- [ ] 3.4 Introduce a test seam for the HTTP call (e.g. an `internal` constructor accepting an `HttpMessageHandler`/`HttpClient`) so the custom-`baseUrl` branch is unit-testable without live network; wire `Dmon.Providers.Tests` via `InternalsVisibleTo` if not already present.

## 4. Tests

- [ ] 4.1 `OpenAiProviderFactoryTests`: custom `baseUrl` with a stubbed handler returns all `data[].id` entries unfiltered (e.g. `llama3.2`, `mlx-community/...`).
- [ ] 4.2 Custom `baseUrl` with null/empty `apiKey` issues the request to `{baseUrl}/models` with no `Authorization` header.
- [ ] 4.3 Custom `baseUrl` failure (non-2xx / thrown / empty `data`) returns an empty list and never the OpenAI fallback list.
- [ ] 4.4 Regression: `baseUrl` null with null `apiKey` still returns the static fallback list, and a successful OpenAI-cloud fetch still applies the `gpt-`/`o`+digit filter.
- [ ] 4.5 `ModelModelsHandler` test (in `Dmon.Core.Tests`) asserts the configured `ProviderConfig.BaseUrl` is forwarded to the factory.

## 5. Gates

- [ ] 5.1 `make build` clean (no warnings; `TreatWarningsAsErrors`).
- [ ] 5.2 `make test` green (new and existing tests).
- [ ] 5.3 `openspec validate openai-adapter-custom-baseurl-models --strict` passes.
