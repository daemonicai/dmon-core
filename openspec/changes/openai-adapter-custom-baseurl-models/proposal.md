## Why

The `openai` adapter is the de-facto bridge to every OpenAI-compatible local server — the shipped `.dmon/config.yaml` wires `llama.cpp`, `oMLX`, and an `ollama` alias through it with a custom `baseUrl` and `auth.type: none`. But `OpenAiProviderFactory.GetAvailableModelsAsync` ignores `baseUrl` entirely: it always queries `https://api.openai.com/v1/models`, and when no API key is present it short-circuits to a hardcoded OpenAI fallback list (`gpt-4o`, `gpt-4o-mini`, `o3`). The result is that `/model` for any OpenAI-compatible local endpoint offers three OpenAI cloud models that the server cannot serve, instead of the models actually loaded locally.

## What Changes

- `OpenAiProviderFactory.GetAvailableModelsAsync` honours a configured custom `baseUrl`: when one is set it queries `{baseUrl}/models` (the OpenAI-compatible listing endpoint) rather than the hardcoded OpenAI host.
- The static OpenAI fallback list is returned **only** when the factory is targeting the real OpenAI endpoint (no custom `baseUrl`). For a custom endpoint, a failed/empty fetch returns an empty list so the picker reports "no models" rather than offering unservable OpenAI cloud models.
- Authentication is honoured per config: the `Authorization: Bearer` header is sent only when an API key is present, so `auth.type: none` endpoints are queried unauthenticated.
- The factory gains access to the configured `baseUrl` (and effective auth) for the listing call. **BREAKING** if this requires widening `IProviderFactory.GetAvailableModelsAsync` — the design phase selects the threading mechanism and whether the interface signature changes.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `provider-model-listing`: The "OpenAiProviderFactory fetches models from the OpenAI REST API" requirement changes to honour a custom `baseUrl` and to scope the static fallback list to the real OpenAI endpoint only. If the interface signature widens to carry `baseUrl`, the "IProviderFactory exposes async model listing" requirement changes too.

## Impact

- `src/Dmon.Providers/OpenAiProviderFactory.cs` — listing endpoint, auth header, and fallback gating.
- `src/Dmon.Abstractions/Providers/IProviderFactory.cs` — possible signature change to thread `baseUrl` (design decision).
- `src/Dmon.Core/Providers/ModelModelsHandler.cs` — passes the configured `baseUrl` to the factory.
- Other `IProviderFactory` implementors (`AnthropicProviderFactory`, `GeminiProviderFactory`, `OllamaProviderFactory`) if the interface signature changes.
- Fixes `/model` discovery for `llama.cpp`, `oMLX`, and any `openai`-adapter endpoint with a custom `baseUrl`.
