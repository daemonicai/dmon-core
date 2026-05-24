# ADR-007: Provider Extension Lifecycle

**Date:** 2026-05-24
**Status:** Accepted

## Context

The V1 architecture supports a fixed set of cloud providers (Anthropic, OpenAI, Gemini) configured via API keys. As local inference becomes practical — Ollama, MLX, llama.cpp, Jan — users will want to run models on their own hardware without cloud accounts. Each local runner has different platform prerequisites (CUDA, Metal, a running server process), different authentication requirements, and a dynamic model catalogue that changes at runtime.

Hardcoding these runners in `Dmon.Core` would violate the extension model (ADR-002) and would not scale. Instead, local runners are packaged as NuGet extensions that implement `IProviderExtension`. The extension loader already handles code security analysis and approval gating (ADR-006); provider extensions reuse the same pipeline and add a lifecycle layer on top.

Several decisions need to be consistent across all provider extension implementations. This ADR records those decisions so future extension authors and the core team have a single source of truth.

## Decisions

### 1. `IsApplicable()` is called once at load time

`IsApplicable()` returns `false` when the current platform or hardware cannot support the provider — for example, an MLX extension on a non-Apple-Silicon machine, or an extension that requires a GPU when none is present. It is a synchronous, cheap check: inspect CPU architecture, check for required binaries, verify minimum OS version. It must not perform network I/O or start any process.

When `IsApplicable()` returns `false`, the extension loader logs a `Warning`:

```
Provider extension '{Name}' is not applicable on this platform and will not be activated.
```

The extension is **not** registered in `ProviderRegistry`. The ADR-006 pipeline approval (`package-id@version` key) is still stored — the user opted in to the extension; the hardware is simply not ready. A future machine where `IsApplicable()` returns `true` will activate without re-prompting (subject to version matching).

### 2. `EnsureRunningAsync()` is gated behind an ADR-006 confirmation prompt

Starting a local inference server has side effects: process creation, network port binding, disk writes, potential download of model weights. Under ADR-006 these are non-implicit operations that require user confirmation.

`EnsureRunningAsync()` is therefore never called speculatively. The daemon calls it only after presenting the user with a `tool.confirmRequest` (ADR-003) carrying `risk: high`. This prompt explains that the provider server will be started. The user may allow-once, allow-for-project, allow-globally, or deny.

Extensions must not start processes, bind ports, or perform downloads inside `IsApplicable()`, `IsRunningAsync()`, `ListModelsAsync()`, or `CreateFactory()`. Those methods are called without a preceding prompt.

### 3. Each provider extension supplies its own `IProviderFactory`

`IProviderFactory.CreateChatClientAsync` receives a `ProviderConfig` containing auth credentials (ADR-005). Local runners have meaningfully different auth requirements:

- Ollama running on `localhost` requires no authentication.
- A remote Ollama endpoint behind a reverse proxy may require a bearer token.
- MLX Server exposes an OpenAI-compatible API with an optional API key.
- A future extension for a private corporate endpoint may use mTLS or a different scheme.

Returning a single shared factory from the core would require the core to understand all these variants. Instead, each `IProviderExtension` returns the factory it controls via `CreateFactory()`. The factory is registered in `ProviderRegistry` under `factory.AdapterName`. Per-runner isolation is the correct boundary: the extension author knows the auth shape; the daemon core does not need to.

### 4. Model capabilities are inferred from the model identifier

`ListModelsAsync()` returns `ModelInfo` records containing a `ChatClientCapabilities` instance. Local runners typically do not expose a capabilities API — they list model names and sizes. The extension is responsible for constructing `ChatClientCapabilities` from what it knows, using a heuristic based on the model identifier.

Recommended heuristic: if the model ID contains a reasoning signal (e.g. `qwq`, `deepseek-r1`, `o1`) set `SupportsReasoning = true`; if the model is known to support tool calling (e.g. Mistral, Llama 3.1+, Qwen 2.5) set `SupportsToolCalling = true`. Context-window estimates can be derived from the model family and quantisation suffix.

This heuristic lives in extension code, not in `Dmon.Core`. The daemon core treats `ChatClientCapabilities` as opaque data.

## Consequences

- Extension authors implement one interface (`IProviderExtension`) to integrate a fully-featured local provider. No modifications to `Dmon.Core` or `Dmon.Abstractions` are required per runner.
- `IsApplicable()` failures are silent to the end user beyond the Warning log. Users on unsupported hardware will not see error dialogs — the extension simply stays dormant.
- `EnsureRunningAsync()` is never called without user consent. Extensions that start processes without implementing this pattern correctly will have those calls blocked by the permission layer.
- The capability heuristic is best-effort. A model that advertises tool-calling support but fails at runtime will surface errors at call time, not at registration time. This is acceptable for V1.
- Each local provider is independently versioned and updated through the NuGet extension pipeline. A version bump triggers a new ADR-006 analysis pass, which is intentional — model weight downloads and process management code are high-risk changes worth re-reviewing.
