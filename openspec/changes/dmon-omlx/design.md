## Context

`IProviderExtension` (ADR-007, provider-extensions change) defines the contract for NuGet packages that contribute local-inference providers. `Dmon.Extensions.Omlx` is the reference implementation — the first concrete example of that contract. It adapts oMLX, a macOS-native MLX inference server (https://omlx.ai), which exposes an OpenAI-compatible HTTP API with one non-standard detail: auth uses an `x-api-key` header rather than `Authorization: Bearer`.

The project lives under `extensions/` in this repo initially. Once stable it will be extracted to its own repository and published to nuget.org.

## Goals / Non-Goals

**Goals:**
- Implement every member of `IProviderExtension` for oMLX
- Validate that the `IProviderExtension` contract is sufficient and ergonomic for a real local runner
- Provide a reference that extension authors can follow when building adapters for other runners (Ollama, LM Studio, llama.cpp, etc.)
- Full unit test coverage of platform check, model listing, capability heuristic, and auth injection

**Non-Goals:**
- Supporting non-Apple-Silicon Macs (Intel), Windows, or Linux
- Managing oMLX installation (the app must already be installed)
- Managing model downloads within oMLX
- Supporting oMLX's VLM / embedding endpoints beyond the basic `/v1/chat/completions` surface

## Decisions

### Custom `HttpClientHandler` for `x-api-key`

oMLX requires the API key in the `x-api-key` header, not `Authorization: Bearer`. The OpenAI SDK's `ApiKeyCredential` always emits the Bearer header, so a custom `DelegatingHandler` (`OmlxAuthHandler`) is injected into the `HttpClient` pipeline to add `x-api-key` on every request. This keeps `OmlxProviderFactory.CreateAsync` clean — it constructs an `OpenAIClientOptions` with the custom handler and the configured base URL.

*Alternative considered:* subclass `ApiKeyCredential` — not possible, it's sealed. *Alternative:* fork the OpenAI SDK client — disproportionate for a header injection.

### Server identity via `owned_by`

`IsRunningAsync()` calls `GET /v1/models` with the configured key and checks that at least one entry has `"owned_by": "omlx"`. This confirms the correct server is on the expected port, not something else (Ollama, LM Studio) that happens to be listening.

*Alternative:* check a custom `/health` endpoint — oMLX does not expose one.

### `open -a oMLX` for lifecycle

`EnsureRunningAsync()` launches the oMLX app via `Process.Start("open", "-a oMLX")` and polls `IsRunningAsync()` up to a configurable timeout (default 30 s, 1 s poll interval). The oMLX menu bar app starts the HTTP server on launch.

*Alternative:* `omlx-server` CLI — not present in the current oMLX distribution; menu bar app is the distribution unit.

### Config via environment variables

Port and API key are resolved in priority order:
1. Explicit constructor injection (for tests)
2. `OMLX_BASE_URL` env var (full URL, e.g. `http://localhost:8666`)
3. `OMLX_API_KEY` env var
4. Defaults: `http://localhost:8666`, empty string (oMLX works without a key when auth is disabled)

This follows the ADR-005 pattern used by built-in providers (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, etc.).

### Capability heuristic from model ID

oMLX's `/v1/models` response carries no capability metadata. Capabilities are inferred from the model ID string using pattern matching (case-insensitive substring/prefix checks):

| Pattern | `SupportsToolCalling` | `SupportsReasoning` |
|---------|----------------------|---------------------|
| `*-it-*`, `*instruct*`, `*-chat*` | true | false |
| `*embed*`, `*-e-*`, `*rerank*` | false | false |
| `qwen3*`, `*thinking*`, `*-r1*`, `*reason*` | true | true |
| `*vlm*`, `*vision*`, `*-vl-*` | true | false |
| (unrecognised) | false | false |

Patterns are evaluated in order; first match wins. `ContextWindow` and `MaxTokens` are not inferred — left at 0 (safe defaults in `ChatClientCapabilities`).

## Risks / Trade-offs

- **oMLX port changes** → The base URL is configurable via `OMLX_BASE_URL`. If oMLX changes its default port in a future release, users set the env var. Risk: low.
- **oMLX auth disabled** → If the user has disabled auth in oMLX, the API key is empty. `OmlxAuthHandler` omits the header when the key is empty. Risk: low — this is intentional behaviour.
- **`open -a oMLX` startup latency** → On cold start, oMLX may take 10–20 s to load a large model. The 30 s default timeout covers typical cases; it is configurable. Risk: medium — user experience depends on machine speed.
- **Model ID pattern fragility** → New model families may not match existing patterns, defaulting to conservative (no tool calling). Consequence: agent falls back gracefully. The heuristic is in `OmlxCapabilityHeuristic` (a static class) — easy to update without changing the provider contract. Risk: low.
- **`owned_by` field** → If a future oMLX release changes the `owned_by` value, `IsRunningAsync()` will return false even when the server is running. Mitigation: the check value is a constant `OmlxProviderExtension.OmlxOwnedBy = "omlx"` — visible and easy to update. Risk: low.
