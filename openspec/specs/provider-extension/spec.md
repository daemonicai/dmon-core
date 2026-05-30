# Spec: IProviderExtension

**Status:** Draft  
**Change:** provider-extensions

## Purpose

`IProviderExtension` is implemented by NuGet packages that contribute a local-inference provider (Ollama, oMLX, LM Studio, llama.cpp, etc.) to a running dmon session. It is the second extension kind alongside `IDaemonExtension` (tool extensions).

## Requirements

### Requirement: IProviderExtension interface shape
Every provider extension package SHALL implement `IProviderExtension` with the following contract:

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

#### Scenario: Provider extension exposes all required members
- **WHEN** any NuGet package implementing `IProviderExtension` is loaded
- **THEN** the type exposes `ProviderName`, `IsApplicable`, `IsRunningAsync`, `EnsureRunningAsync`, `ListModelsAsync`, and `CreateFactory` with the signatures above

### Requirement: ProviderName is stable and unique
`ProviderName` SHALL be a human-readable string that is unique per provider (e.g. `"oMLX"`, `"Ollama"`, `"LM Studio"`) and stable across versions of the extension package. It is used as `ProviderConfig.Name` in the registry and passed to `SetProvider(name)`.

#### Scenario: ProviderName used as registry key
- **WHEN** a provider extension is registered
- **THEN** the registry stores the provider under the key equal to `extension.ProviderName`

### Requirement: IsApplicable is synchronous and non-throwing
`IsApplicable()` SHALL be synchronous and MUST NOT block on I/O. It SHALL return `true` if and only if the current runtime platform and hardware can host this provider. Common checks include `RuntimeInformation.IsOSPlatform`, CPU architecture, and GPU availability. It MUST NOT throw; exceptions are caught and treated as `false`.

#### Scenario: Inapplicable provider is not registered
- **WHEN** `IsApplicable()` returns `false` for a loaded extension
- **THEN** a `Warning` log is emitted, the provider is not registered in `IProviderRegistry`, and the approval is persisted so future sessions on compatible hardware skip the security pipeline

#### Scenario: Exception in IsApplicable treated as false
- **WHEN** `IsApplicable()` throws
- **THEN** the exception is caught and the extension is treated as inapplicable

### Requirement: IsRunningAsync verifies server identity
`IsRunningAsync(cancellationToken)` SHALL return `true` if the inference server is reachable and has the expected identity. Implementations MUST verify server identity (e.g. check `owned_by` in `/v1/models` response), not merely that a port is open. Implementations SHALL use a short internal timeout (≤ 2 s) so the caller is not blocked. It MUST NOT throw; exceptions are caught and treated as `false`.

#### Scenario: Running server with correct identity returns true
- **WHEN** `IsRunningAsync` is called and the server responds with the expected identity
- **THEN** it returns `true` within 2 seconds

#### Scenario: Port open but wrong server returns false
- **WHEN** `IsRunningAsync` is called and a server is reachable at the port but does not identify as the expected provider
- **THEN** it returns `false`

#### Scenario: Exception in IsRunningAsync treated as false
- **WHEN** `IsRunningAsync` throws (e.g. network error)
- **THEN** the exception is caught and the result is treated as `false`

### Requirement: EnsureRunningAsync launches the server if needed
`EnsureRunningAsync(cancellationToken)` SHALL be called by the daemon only after the user has confirmed via an ADR-006 prompt. The extension declares the start mechanism (CLI command, app launch); the daemon owns the prompt. If the server is already running this is a no-op. It SHALL poll until the server is reachable up to a reasonable timeout. It MAY throw `TimeoutException` if the server does not become reachable within the timeout.

#### Scenario: Server already running is a no-op
- **WHEN** `EnsureRunningAsync` is called and `IsRunningAsync` already returns `true`
- **THEN** it returns immediately without launching or waiting

#### Scenario: Server not running is launched and polled
- **WHEN** `EnsureRunningAsync` is called and the server is not running
- **THEN** the extension launches the server via its declared mechanism and polls until it is reachable

#### Scenario: Timeout raises TimeoutException
- **WHEN** the server does not become reachable within the polling timeout
- **THEN** `EnsureRunningAsync` throws `TimeoutException`

### Requirement: ListModelsAsync returns available models with capability metadata
`ListModelsAsync(cancellationToken)` SHALL return the models currently available from the running server. Each `ModelInfo` SHALL include a `ChatClientCapabilities` derived from the model ID via the capability heuristic. If the server is not running, implementations MAY return an empty list or throw; callers handle both.

```csharp
public sealed record ModelInfo
{
    public required string Id { get; init; }
    public required ChatClientCapabilities Capabilities { get; init; }
}
```

`Id` is the model identifier as returned by the runner's `/v1/models` endpoint (or equivalent).

#### Scenario: Running server returns model list
- **WHEN** `ListModelsAsync` is called and the server is running
- **THEN** it returns one `ModelInfo` per available model with `Id` set from the server response

#### Scenario: Server not running returns empty list or throws
- **WHEN** `ListModelsAsync` is called and the server is not reachable
- **THEN** it returns an empty list or throws; callers MUST handle both outcomes

### Requirement: CreateFactory returns a provider factory without performing I/O
`CreateFactory()` SHALL return an `IProviderFactory` that the registry uses to create `IChatClient` instances. The factory is responsible for correct HTTP auth for its runner (which may differ from the standard `Authorization: Bearer` pattern). `CreateFactory()` SHALL be called only after `IsApplicable()` returns `true` and MUST NOT perform I/O.

#### Scenario: Factory returned without I/O
- **WHEN** `CreateFactory()` is called after a successful `IsApplicable()` check
- **THEN** an `IProviderFactory` is returned synchronously without network or disk access

### Requirement: Capability heuristic derives ChatClientCapabilities from model ID
Because local runners expose no capability metadata in their model lists, implementations SHALL derive `ChatClientCapabilities` from the model ID using the following pattern table (case-insensitive, first match wins):

| Pattern | Inference |
|---------|-----------|
| `*-it-*`, `*-instruct*`, `*-chat*` | Tool calling plausible |
| `*embed*`, `*-e-*`, `*rerank*` | Embedding / reranker — no tool calling, no reasoning |
| `*reason*`, `qwen3*`, `*thinking*`, `*r1*` | Reasoning likely supported |
| `*vlm*`, `*vision*`, `*vl-*` | Vision — multimodal, tool calling plausible |
| Unrecognised | Conservative defaults: `SupportsToolCalling = false`, `SupportsReasoning = false` |

Capability mismatches fail gracefully.

#### Scenario: Instruct pattern infers tool calling
- **WHEN** the capability heuristic is applied to a model ID matching `*-instruct*`
- **THEN** `SupportsToolCalling = true` and `SupportsReasoning = false`

#### Scenario: Reasoning pattern infers reasoning support
- **WHEN** the capability heuristic is applied to a model ID matching `qwen3*` or `*reason*`
- **THEN** `SupportsReasoning = true`

#### Scenario: Unrecognised model ID uses conservative defaults
- **WHEN** the capability heuristic is applied to a model ID that matches no pattern
- **THEN** `SupportsToolCalling = false` and `SupportsReasoning = false`

### Requirement: Extension lifecycle follows the registered pipeline
When the daemon loads a NuGet package that implements `IProviderExtension`, it SHALL follow the lifecycle below:

1. Source fetch, LLM security analysis, and user confirmation.
2. Assembly loaded into `AssemblyLoadContext`.
3. `IsApplicable()` called: if `false`, a `Warning` log is emitted, approval is stored at `package@version`, and `ExtensionLoadedEvent` is emitted with `ProviderName = null`.
4. If `true`: `IProviderRegistry.RegisterExtensionAsync(extension)` is called, which calls `CreateFactory()`, calls `ListModelsAsync()` to obtain the `DefaultModelId`, synthesises a `ProviderConfig`, and registers the factory and config. `ExtensionLoadedEvent` is emitted with `ProviderName = extension.ProviderName`.

#### Scenario: Applicable extension is registered and event emitted
- **WHEN** `IsApplicable()` returns `true` for a loaded extension
- **THEN** `IProviderRegistry.RegisterExtensionAsync` is called and `ExtensionLoadedEvent` is emitted with `ProviderName` set to the extension's provider name

#### Scenario: Inapplicable extension emits event with null ProviderName
- **WHEN** `IsApplicable()` returns `false` for a loaded extension
- **THEN** `ExtensionLoadedEvent` is emitted with `ProviderName = null` and the provider is not registered

### Requirement: Discovery and packaging constraints
Extension NuGet packages that implement `IProviderExtension` SHALL be tagged `dmon-extension` on nuget.org to appear in `extension.search` results. A package MAY implement both `IDmonExtension` (contributing tools) and `IProviderExtension` (contributing a provider); both are registered. The package MUST declare a `<repository url="..." commit="...">` in its nuspec (source availability gate, per extension-ecosystem change). GitHub enrichment (`extension.readme` README fetch) applies to provider extension packages in the same way as tool extension packages.

#### Scenario: Provider extension package is discoverable via extension.search
- **WHEN** a NuGet package tagged `dmon-extension` implements `IProviderExtension`
- **THEN** it appears in `extension.search` results

#### Scenario: Dual-purpose package registers both extension kinds
- **WHEN** a package implements both `IDmonExtension` and `IProviderExtension`
- **THEN** both the tool extension and the provider extension are registered independently
