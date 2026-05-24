# Proposal: Provider Extensions

**Slug:** provider-extensions  
**Status:** Draft  
**Date:** 2026-05-24  
**Depends on:** extension-ecosystem (Groups 4–5 must be complete before implementation)

## Summary

Add `IProviderExtension` as a second extension kind alongside `IDaemonExtension`. NuGet packages tagged `dmon-extension` may implement `IProviderExtension` to contribute a local-inference provider — including platform checks, server lifecycle management, and dynamic model listing — without touching the daemon core. The reference implementation is `dmon-omlx`.

## Motivation

Local inference runners are proliferating across platforms:

- **macOS (Apple Silicon):** oMLX, Ollama, LM Studio, mlx-lm, mlx-vlm, llama.cpp
- **Linux (CUDA):** llama.cpp, koboldcpp, vllm, text-generation-inference
- **Linux (ROCm / OpenVINO):** llama.cpp, OpenVINO model server
- **Windows:** LM Studio, Ollama, llama.cpp

Each is platform- and hardware-specific, each has different auth conventions, health endpoints, start commands, and model-ID formats. Building in support for all of them would bloat the core and could never keep pace with new entrants. The extension ecosystem — NuGet as registry, `dmon-extension` tag as opt-in signal — is the right distribution mechanism.

The built-in `IProviderFactory` interface is designed for cloud providers: it takes an API key and a config, and returns an `IChatClient`. It has no concept of server lifecycle, platform applicability, or dynamic model listing. Local runners need all three.

## Decisions made

### `IProviderExtension` is the interface name

Not `ILocalRunnerExtension`. The name doesn't bake in the "local" assumption, which leaves room for other provider extension patterns that may emerge. In practice, all V1 implementations will be local runners.

### `IsApplicable()` is called at extension load time

When the extension-ecosystem pipeline loads a `IProviderExtension` assembly, it calls `IsApplicable()` immediately. If the result is `false`, a `Warning`-level log message is emitted and the provider is not registered. The security-pipeline approval (`package@version`) is still stored, so a user who installs `dmon-llamacpp-cuda` on a Mac today pays the security-analysis cost once — if they later run on a CUDA machine, the provider activates without re-prompting.

### `EnsureRunningAsync()` gates on an ADR-006 confirmation prompt

The extension declares *how* to start the server (CLI command, app launch, etc.); the daemon owns the prompt. The user is asked once (with "always" as an option) before the start command is run. This is consistent with the permission model in ADR-006.

### Each runner needs its own factory

Local runners vary significantly in their HTTP auth conventions. oMLX uses an `x-api-key` header; Ollama uses no auth by default; LM Studio uses `Authorization: Bearer`. The OpenAI-compatible adapter cannot be used with just a `BaseUrl` config entry for runners that deviate from the Bearer pattern. `IProviderExtension.CreateFactory()` returns a runner-specific `IProviderFactory` that handles auth correctly.

### Model listing is dynamic

Local runners serve whatever models the user has downloaded. `ListModelsAsync()` queries the runner's `/v1/models` endpoint (or equivalent). The response is standard OpenAI list shape for all OpenAI-compatible runners:

```json
{
  "object": "list",
  "data": [
    { "id": "gemma-4-e4b-it-4bit", "object": "model", "owned_by": "omlx" }
  ]
}
```

The `owned_by` field can be used as a server identity signal during `IsRunningAsync()` — a probe that verifies the expected runner is actually on that port.

### Capabilities are inferred from model ID

No capability metadata is included in the `/v1/models` response. Each `IProviderExtension` implementation is responsible for a capability heuristic based on model ID patterns:

- `*-it-*` / `*-instruct*` → tool calling plausible
- `*embed*` / `*-e-*` → embedding-only, no tool calling
- `*reason*` / `qwen3*` / `*thinking*` → reasoning likely supported
- Unrecognised → conservative defaults (no tool calling, no reasoning)

This is a best-effort heuristic. Mismatches fail gracefully: the model declines tool calls, the agent handles the fallback.

### GitHub signals do not apply to `IProviderExtension`

The `extension.search` and `extension.readme` enrichment via `IGhCliService` applies to tool extensions (`IDaemonExtension`). Provider extensions are found and evaluated the same way, but the GitHub enrichment signals (stars, last push, archived) are less meaningful for local runner adapters — these are thin wrappers around specific servers, not general-purpose tools. The enrichment step is skipped; NuGet signals (downloads, updated timestamp) are sufficient.

## Interface

```csharp
/// <summary>
/// Implemented by NuGet extensions that contribute a local-inference provider.
/// </summary>
public interface IProviderExtension
{
    /// <summary>Human-readable name for this provider, e.g. "oMLX".</summary>
    string ProviderName { get; }

    /// <summary>
    /// Returns true if this provider can run on the current platform and hardware.
    /// Called once at extension load time. If false, a Warning is logged and
    /// the provider is not registered. The extension approval is still stored.
    /// </summary>
    bool IsApplicable();

    /// <summary>
    /// Returns true if the server is currently reachable at its expected endpoint.
    /// Implementations should verify server identity (e.g. via owned_by in /v1/models)
    /// not just that something is listening on the port.
    /// </summary>
    Task<bool> IsRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the server. Only called after an ADR-006 confirmation prompt.
    /// The implementation declares the start mechanism; the daemon owns the prompt.
    /// </summary>
    Task EnsureRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the list of models currently available from the running server.
    /// </summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an IProviderFactory configured for this runner.
    /// Called after IsRunningAsync() confirms the server is up.
    /// </summary>
    IProviderFactory CreateFactory();
}
```

## Reference implementation: dmon-omlx

The first `IProviderExtension` is `dmon-omlx`, a thin adapter for [oMLX](https://omlx.ai) — a macOS-native MLX inference server for Apple Silicon.

- **`IsApplicable()`** — checks `RuntimeInformation.IsOSPlatform(OSPlatform.OSX)` and Apple Silicon architecture
- **`IsRunningAsync()`** — `GET http://localhost:{port}/v1/models` with `x-api-key` header; confirms `owned_by == "omlx"` in at least one result
- **`EnsureRunningAsync()`** — opens the oMLX app via `open -a oMLX` (macOS); polls until server responds, up to a configurable timeout
- **`ListModelsAsync()`** — queries `/v1/models`, maps to `ModelInfo` with capability heuristic on model ID
- **`CreateFactory()`** — returns an `OmlxProviderFactory` that injects `x-api-key` via a custom `HttpClientHandler`

`dmon-omlx` lives in a separate repository and is published to nuget.org tagged `dmon-extension`. It serves as validation that the full extension-ecosystem pipeline (search → readme → load → IsApplicable → register) works end to end for provider extensions.

## Out of scope

- Cloud providers as extensions (Anthropic, OpenAI, Gemini remain built-in)
- Multi-model serving coordination across runners
- Extension-managed model download or quantisation
- Health monitoring or auto-restart after crash
- Private/authenticated model registries
