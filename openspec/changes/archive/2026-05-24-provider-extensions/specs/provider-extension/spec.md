# Spec: IProviderExtension

**Status:** Draft  
**Change:** provider-extensions

## Purpose

`IProviderExtension` is implemented by NuGet packages that contribute a local-inference provider (Ollama, oMLX, LM Studio, llama.cpp, etc.) to a running dmon session. It is the second extension kind alongside `IDaemonExtension` (tool extensions).

## Interface contract

```csharp
public interface IProviderExtension
{
    string ProviderName { get; }
    bool IsApplicable();
    Task<bool> IsRunningAsync(CancellationToken cancellationToken = default);
    Task EnsureRunningAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);
    IProviderFactory CreateFactory();
}
```

## Lifecycle

```
Extension pipeline
    │
    ├─ source fetch
    ├─ LLM security analysis
    ├─ report → user confirm
    ├─ assemble into AssemblyLoadContext
    │
    └─ Detect IProviderExtension
           │
           ├─ IsApplicable() == false
           │       → logger.LogWarning(...)
           │       → approval stored at package@version
           │       → ExtensionLoadedEvent emitted (ProviderName = null)
           │
           └─ IsApplicable() == true
                   → IProviderRegistry.RegisterExtensionAsync(extension)
                       ├─ CreateFactory()
                       ├─ ListModelsAsync() → DefaultModelId
                       ├─ Synthesise ProviderConfig
                       └─ Register factory + config
                   → ExtensionLoadedEvent emitted (ProviderName = extension.ProviderName)
```

## Member specifications

### `ProviderName`

- Human-readable, unique per provider, e.g. `"oMLX"`, `"Ollama"`, `"LM Studio"`.
- Used as the `ProviderConfig.Name` in the registry; passed to `SetProvider(name)`.
- Must be stable across versions of the extension package.

### `IsApplicable()`

- **Synchronous.** Must not block on I/O.
- Returns `true` if and only if the current runtime platform and hardware can host this provider.
- Common checks: `RuntimeInformation.IsOSPlatform`, CPU architecture, GPU availability.
- If `false`: a `Warning` log is emitted, the provider is not registered. The approval is persisted so future sessions on compatible hardware skip the security pipeline.
- Must not throw. Exceptions are caught and treated as `false`.

### `IsRunningAsync(cancellationToken)`

- Returns `true` if the inference server is reachable and has the expected identity.
- Implementations must verify server identity (e.g. check `owned_by` in `/v1/models` response), not just that a port is open.
- Timeout: implementations should use a short internal timeout (≤ 2 s) so the caller is not blocked.
- Must not throw. Exceptions are caught and treated as `false`.

### `EnsureRunningAsync(cancellationToken)`

- Called by the daemon only after the user has confirmed via an ADR-006 prompt.
- The extension declares the start mechanism (CLI command, app launch); the daemon owns the prompt.
- If the server is already running, this is a no-op.
- Should poll until the server is reachable, up to a reasonable timeout.
- May throw `TimeoutException` if the server does not become reachable within the timeout.

### `ListModelsAsync(cancellationToken)`

- Returns models currently available from the running server.
- If the server is not running, implementations may return an empty list or throw. Callers handle both.
- Each `ModelInfo` includes a `ChatClientCapabilities` derived from the model ID via heuristic (see below).

### `CreateFactory()`

- Returns an `IProviderFactory` that the registry will use to create `IChatClient` instances.
- The factory is responsible for correct HTTP auth for its runner (which may differ from the standard `Authorization: Bearer` pattern).
- Called after `IsApplicable()` returns true; may assume the provider is applicable.
- Must not perform I/O.

## `ModelInfo`

```csharp
public sealed record ModelInfo
{
    public required string Id { get; init; }
    public required ChatClientCapabilities Capabilities { get; init; }
}
```

`Id` is the model identifier as returned by the runner's `/v1/models` endpoint (or equivalent).

## Capability heuristic

Because local runners expose no capability metadata in their model lists, implementations should derive `ChatClientCapabilities` from the model ID using patterns:

| Pattern | Inference |
|---------|-----------|
| `*-it-*`, `*-instruct*`, `*-chat*` | Tool calling plausible |
| `*embed*`, `*-e-*`, `*rerank*` | Embedding / reranker — no tool calling, no reasoning |
| `*reason*`, `qwen3*`, `*thinking*`, `*r1*` | Reasoning likely supported |
| `*vlm*`, `*vision*`, `*vl-*` | Vision — multimodal, tool calling plausible |
| Unrecognised | Conservative defaults: `SupportsToolCalling = false`, `SupportsReasoning = false` |

This is best-effort. Capability mismatches fail gracefully.

## Discovery and packaging

- Extension NuGet packages must be tagged `dmon-extension` on nuget.org to appear in `extension.search` results.
- A package may implement both `IDmonExtension` (contributing tools) and `IProviderExtension` (contributing a provider); both are registered.
- The package must declare a `<repository url="..." commit="...">` in its nuspec (source availability gate, per extension-ecosystem change).
- GitHub enrichment (`extension.readme` README fetch) applies to provider extension packages in the same way as tool extension packages.
