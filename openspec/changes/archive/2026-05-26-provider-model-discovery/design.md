## Context

The provider setup wizard in `Dmon.Terminal` (and the parallel surface in `Dmon.Tui`) presents a static `Dictionary<string, string[]>` of model IDs keyed by adapter name. The list is embedded in `WizardSteps.cs` and must be manually updated whenever a provider releases a new model. Additionally, the wizard presents models before collecting the API key, so live fetching was previously impossible.

All three provider REST APIs expose a model-listing endpoint that requires only the API key that the wizard already collects.

Current wizard step order:
1. Select Adapter
2. **Select Model** ← hard-coded list, no key available
3. Auth Config (collect API key / env var)

## Goals / Non-Goals

**Goals:**
- Add `GetAvailableModelsAsync(string? apiKey, CancellationToken)` to `IProviderFactory` so each factory can return its live model list.
- Implement for all three built-in factories (Anthropic, OpenAI, Gemini) with graceful static fallback on failure.
- Reorder the wizard to Auth Config → Model Selection so the resolved key is available when models are fetched.
- Add a transient `ResolvedApiKey` field to `WizardState` (in-memory only, never persisted).

**Non-Goals:**
- Caching model lists between wizard runs (no persistent cache in V1).
- Filtering or annotating models beyond the existing `ChatClientCapabilities` inference (no per-model pricing, rate limits, etc.).
- OAuth or other non-key auth flows (ADR-005).
- Extension (`IDaemonExtension`) providers — this change only touches the three built-in `IProviderFactory` implementations.

## Decisions

### D1: Method on `IProviderFactory` rather than a separate `IModelLister` interface

**Decision:** Add `GetAvailableModelsAsync` directly to `IProviderFactory`.

**Rationale:** Every factory already knows its endpoint, its auth header shape, and its static fallback list. A second interface would require double registration and null-checks at call sites. The method can default-implement to return the static list, minimising breakage for external implementors.

**Alternative considered:** Separate `IModelLister` registered independently in DI. Rejected because it doubles the number of types without meaningful gain at this stage.

### D2: Wizard reorder — Auth before Model

**Decision:** Swap steps 2 and 3: Auth Config becomes step 2; Model Selection becomes step 3.

**Rationale:** The live model fetch needs the API key. Reordering is the only clean option — we don't want to re-prompt for the key, and we don't want to make assumptions about env var presence.

**Alternative considered:** Try the env var silently at model-select time, falling back to the static list if it is unset. Rejected because it produces inconsistent UX: users with the key set see a live list; others see the static list with no explanation. Reordering makes the behaviour predictable.

### D3: `ResolvedApiKey` on `WizardState`, not injected as a service

**Decision:** Add `string? ResolvedApiKey` to the `WizardState` record. The auth step resolves the key value (from the env var name the user entered) and stores it there. The model step reads it.

**Rationale:** `WizardState` is already the immutable state carrier for the wizard pipeline. The field is transient — it is never written to the provider config file or any persistent store.

### D4: Plain `HttpClient` for model-listing calls, no new NuGet dependency

**Decision:** Each factory creates an `HttpClient` internally for the single model-listing call. No new SDK dependency.

**Rationale:** The model-listing endpoints are simple GET requests returning JSON. All three responses can be parsed with `System.Text.Json`. The `HttpClient` is short-lived (created, used, disposed per wizard run) — no `IHttpClientFactory` needed at this scale.

**Alternative considered:** Reuse the SDK clients (Anthropic.SDK, GeminiDotnet, OpenAI) for model listing if they expose the endpoint. Rejected because SDK support for model listing is inconsistent and adds SDK-specific error handling surface.

### D5: Static fallback list on any failure, no error surfaced to user

**Decision:** If the HTTP call fails for any reason (bad key, network error, timeout), `GetAvailableModelsAsync` returns the same static list currently hard-coded in `WizardSteps.cs`. No error message is shown; the wizard continues normally.

**Rationale:** The wizard is a first-run flow. Surfacing an API error at model selection is confusing — the user hasn't even confirmed whether their key is correct yet. A silent fallback is the most forgiving path.

## Risks / Trade-offs

- **Stale static fallback**: The hard-coded fallback list can still drift from reality. Mitigation: treat the static list as a "known-good baseline" rather than exhaustive; the live fetch is the primary path.
- **Latency**: The model-listing call adds a short delay at step 3. Mitigation: show a brief `Fetching models…` status line while the call is in flight; the call should complete in under 1 s on a normal connection.
- **Breaking change on `IProviderFactory`**: External implementors (e.g. the `omlx` extension) must add the new method. Mitigation: provide a default implementation returning the static list so existing implementations compile without change.

## Migration Plan

1. Add the method with a `virtual` / default implementation to `IProviderFactory` (or an extension method) so existing external factories continue to compile.
2. Implement in the three built-in factories.
3. Update `WizardState` and `WizardSteps.cs` in `Dmon.Terminal` and `Dmon.Tui`.
4. Update the `omlx` extension if it implements `IProviderFactory` directly — check and add a no-op implementation.
5. Build and run `dotnet test` — no new test fixtures required for the wizard reorder, but integration smoke tests should pass.

## Open Questions

- None outstanding — the design is complete.
