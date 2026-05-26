## 1. Extend IProviderFactory with GetAvailableModelsAsync

- [x] 1.1 Add `ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string? apiKey, CancellationToken cancellationToken = default)` to `IProviderFactory` with a default interface implementation that returns an empty list (concrete factories will override with their static fallback)
- [x] 1.2 Verify the project builds without errors after the interface change — existing external implementors should compile via the default implementation

## 2. Implement GetAvailableModelsAsync in built-in factories

- [x] 2.1 Implement `GetAvailableModelsAsync` in `GeminiProviderFactory`: call `GET https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}`, strip the `models/` prefix, filter to entries starting with `gemini`, fall back to static list on any failure or null/empty key; apply a 5-second timeout
- [x] 2.2 Implement `GetAvailableModelsAsync` in `AnthropicProviderFactory`: call `GET https://api.anthropic.com/v1/models` with `x-api-key` and `anthropic-version: 2023-06-01` headers, parse `data[].id`, fall back to static list on any failure or null/empty key; apply a 5-second timeout
- [x] 2.3 Implement `GetAvailableModelsAsync` in `OpenAiProviderFactory`: call `GET https://api.openai.com/v1/models` with `Authorization: Bearer {apiKey}`, filter `data[].id` to entries starting with `gpt-` or `o`, fall back to static list on any failure or null/empty key; apply a 5-second timeout
- [x] 2.4 Define the static fallback list in each factory as a private static field mirroring the values currently hard-coded in `WizardSteps.cs`

## 3. Update WizardState

- [x] 3.1 Add `string? ResolvedApiKey` property to the `WizardState` record in `Dmon.Terminal` (in-memory only; not serialised or persisted)

## 4. Reorder wizard steps in Dmon.Terminal

- [x] 4.1 Move `AuthConfigStep` to run as step 2 (immediately after adapter selection) in the wizard pipeline in `Dmon.Terminal`
- [x] 4.2 Update `AuthConfigStep.RunAsync` in `Dmon.Terminal` to resolve the actual API key from the environment after the user enters the env var name, and store it in `WizardState.ResolvedApiKey`
- [x] 4.3 Move `ModelSelectionStep` to run as step 3 in `Dmon.Terminal`; update it to inject the appropriate `IProviderFactory`, call `GetAvailableModelsAsync(state.ResolvedApiKey, cancellationToken)`, show a `Fetching models…` status line while the call is in flight, and fall back to the static list if the result is empty
- [x] 4.4 Remove the hard-coded `ModelsByAdapter` dictionary from `ModelSelectionStep` in `Dmon.Terminal` (the static fallback now lives in each factory)

## 5. Verify and clean up

- [x] 5.1 Run `dotnet build` — zero warnings, zero errors
- [x] 5.2 Run `dotnet test` — all tests pass
- [x] 5.3 Manually run the wizard in `Dmon.Terminal` with a valid Gemini API key set in the environment and confirm the live model list appears in step 3
- [x] 5.4 Manually run the wizard with an invalid/missing key and confirm the static fallback list appears without an error message
