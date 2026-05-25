## Why

The provider setup wizard presents a static, hard-coded list of models for each adapter (Anthropic, OpenAI, Gemini). This means newly-released models are invisible until the list is manually updated in code, and the wizard cannot reflect what models the user's API key actually has access to. Fetching the live model list from each provider's API at setup time fixes both problems.

## What Changes

- Add `GetAvailableModelsAsync(string? apiKey, CancellationToken)` to `IProviderFactory`, returning `IReadOnlyList<ModelInfo>` (falls back to a static list on any failure).
- Implement the method in `GeminiProviderFactory`, `AnthropicProviderFactory`, and `OpenAIProviderFactory`, each calling its provider's REST model-listing endpoint.
- Reorder the setup wizard steps: **Adapter → Auth Config → Model Selection**, so the resolved API key is available when the model list is fetched.
- Add a transient `ResolvedApiKey` field to `WizardState` (in-memory only; never persisted).
- Update `WizardSteps.cs` in both `Dmon.Terminal` and `Dmon.Tui` to reflect the new step order and dynamic model fetching.

## Capabilities

### New Capabilities

- `provider-model-listing`: The ability for a provider factory to enumerate its available models via the provider's API, with a static fallback list when the API call fails or no key is available.

### Modified Capabilities

- `provider-factories`: Each factory now implements an additional interface method. The wizard step ordering and `WizardState` shape change.
- `terminal-host`: Setup wizard step order changes (Auth before Model); model list is fetched live.

## Impact

- **`Dmon.Abstractions`**: `IProviderFactory` gains one new method — **BREAKING** for any external implementors.
- **`Dmon.Providers`**: `GeminiProviderFactory`, `AnthropicProviderFactory`; `OpenAIProviderFactory` added if it does not exist.
- **`Dmon.Terminal`**: `WizardSteps.cs`, `WizardState` record.
- **`Dmon.Tui`**: `WizardSteps.cs`, `WizardState` record (parallel surface to Terminal).
- **No new NuGet dependencies** — all three provider REST endpoints are plain JSON over HTTPS, callable via the `HttpClient` already available in the runtime.
