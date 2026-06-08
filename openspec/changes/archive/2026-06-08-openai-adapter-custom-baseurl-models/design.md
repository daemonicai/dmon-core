## Context

`OpenAiProviderFactory` serves two distinct roles today:

1. The real OpenAI cloud endpoint (`https://api.openai.com/v1`), authenticated with `OPENAI_API_KEY`.
2. A generic bridge to any OpenAI-compatible local server (`llama.cpp`, `oMLX`, `ollama`'s `/v1` shim), configured in `config.yaml` with a custom `baseUrl` and usually `auth.type: none`.

`CreateAsync` already honours role 2 — it reads `config.BaseUrl` and points the `OpenAIClient` at it. But `GetAvailableModelsAsync(string? apiKey, CancellationToken)` only knows about role 1. Its signature carries no `baseUrl`, so it hardcodes `https://api.openai.com/v1/models`, and when `apiKey` is null/empty (the `auth.type: none` case) it returns the static OpenAI fallback list (`gpt-4o`, `gpt-4o-mini`, `o3`). A user pointing the adapter at a local server therefore sees three OpenAI cloud models the server cannot run.

`ModelModelsHandler` already holds the `ProviderConfig` (with `BaseUrl`) when it calls the factory — the connection info exists at the call site; it just isn't passed through.

Constraint: `IProviderFactory.GetAvailableModelsAsync` has a default interface implementation (returns empty) so external implementors need not change. Per project policy there are no production deployments, so a clean break is acceptable — but preserving the default keeps the surface minimal.

## Goals / Non-Goals

**Goals:**
- `/model` for an `openai`-adapter provider with a custom `baseUrl` lists the models the configured server actually serves.
- The static OpenAI fallback list is reachable only for the real OpenAI endpoint, never offered for a custom endpoint.
- Unauthenticated (`auth.type: none`) custom endpoints are queried without an `Authorization` header.
- No change to `CreateAsync`, the wizard flow, or other provider factories' observable behaviour.

**Non-Goals:**
- Inferring rich `ChatClientCapabilities` (tool-calling, reasoning, context window) for arbitrary local models — `GetCapabilities` keeps its current coarse heuristics; the picker only needs model IDs.
- Reworking the `OllamaProviderFactory`, which has its own native listing path and is unaffected.
- Changing how credentials/`baseUrl` are stored or resolved in `config.yaml`.

## Decisions

### Decision 1: Thread `baseUrl` via an optional parameter, not a config object

Add an optional `string? baseUrl = null` parameter to `IProviderFactory.GetAvailableModelsAsync`, keeping `cancellationToken` last:

```csharp
ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(
    string? apiKey, string? baseUrl = null, CancellationToken cancellationToken = default);
```

`ModelModelsHandler` passes `config.BaseUrl`. The wizard call sites (`OpenAiProviderFactory.GetNextStepAsync`) pass no `baseUrl` and so keep today's OpenAI-cloud behaviour.

**Alternatives considered:**
- *Pass the whole `ProviderConfig`.* Rejected: couples the listing API to the config record and leaks more than the factory needs; the apiKey is already resolved separately by the handler.
- *Reuse the `apiKey` param to smuggle the URL (the Ollama precedent).* Rejected: `apiKey` and `baseUrl` are genuinely independent for an authenticated OpenAI-compatible proxy, and overloading one string is the kind of implicit coupling that produced this bug.
- *Widen the signature with a required `baseUrl` (no default).* Rejected as needlessly breaking external implementors and the default interface method; the optional form is a clean superset.

### Decision 2: A custom `baseUrl` switches both the endpoint and the fallback/filter policy

Inside `OpenAiProviderFactory.GetAvailableModelsAsync`, branch on whether `baseUrl` is set:

- **No `baseUrl` (real OpenAI):** unchanged — `GET https://api.openai.com/v1/models`, `Authorization: Bearer {apiKey}`, filter IDs to `gpt-*` / `o`+digit, fall back to the static list on null key or any failure.
- **Custom `baseUrl`:** `GET {baseUrl-trimmed}/models`; send `Authorization: Bearer {apiKey}` only when a key is present; return **all** returned IDs unfiltered (local models are not named `gpt-*`); on failure or empty response return an **empty** list (never the OpenAI fallback).

Rationale: the `gpt-/o` filter and the OpenAI fallback list are OpenAI-cloud-specific. Applying the filter to a local server would drop every model; offering the fallback would advertise unservable cloud models. An empty list is the correct, honest signal — `ModelModelsHandler` already surfaces "No models available for {provider}" to the terminal.

### Decision 3: Endpoint construction trims a trailing slash and appends `/models`

The configured `baseUrl` already includes the API version segment (e.g. `http://localhost:8080/v1`). The listing URL is `{baseUrl.TrimEnd('/')}/models`, mirroring how the OpenAI SDK composes request paths against an endpoint. The 5-second timeout and exception-swallowing behaviour are unchanged.

## Risks / Trade-offs

- **[A local server returns non-chat models (embeddings, rerankers) that pollute the picker]** → Accept for now: we cannot reliably classify arbitrary local model names, and listing everything the server serves is more useful than silently dropping models. The capability heuristics in `GetCapabilities` still flag obvious embed/rerank names at selection time.
- **[Empty list when a custom endpoint is unreachable looks identical to "server has no models"]** → Acceptable: both states mean "nothing selectable here". The existing terminal message ("No models available") already covers it; richer diagnostics are out of scope.
- **[Signature change ripples to all `IProviderFactory` implementors]** → The optional parameter with a default keeps existing in-tree implementors compiling unchanged; only `OpenAiProviderFactory` (new behaviour) and `ModelModelsHandler` (passes `config.BaseUrl`) must change. The default interface method covers external implementors.

## Open Questions

None.
