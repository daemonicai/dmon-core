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

### Requirement: Provider extensions have a composition entry point
A provider extension (`IProviderExtension`) SHALL be composed into the host through the `IProviderRegistration` facet, closing the ADR-019 orphan where the deleted dynamic loader was the type's only route into the host. The host builder SHALL expose `AddProvider<T>()` (where `T : IProviderExtension`) as the primitive registration verb, implemented as `Services.AddSingleton<IProviderExtension, T>()`. Each first-party local-inference provider package SHALL additionally ship a fluent `Use<Provider>` verb that calls `AddProvider<T>()` and selects the provider's model via `UseModel`. There SHALL be no separate post-build or dynamic-loading registration path for provider extensions.

#### Scenario: AddProvider registers the extension as a DI singleton
- **WHEN** `AddProvider<LlamaCppProvider>()` is invoked on the host builder
- **THEN** `LlamaCppProvider` is registered as an `IProviderExtension` singleton in the service collection and no dynamic loader is consulted

#### Scenario: Use verb composes a local provider
- **WHEN** a `Dmon.cs` composition root calls `UseLlamaCpp("gemma")`
- **THEN** the verb calls `AddProvider<LlamaCppProvider>()` and selects model `llamacpp/gemma`, with no edit to `Dmon.Core` required to add the provider

### Requirement: Each provider ships as its own granular implementation package
Each local-inference provider SHALL ship as its own granular implementation package named `Dmon.Providers.<Name>`, referencing `Dmon.Abstractions` plus its vendor SDK, and nothing else in the daemon solution. The package's fluent `Use<Provider>` verb SHALL live in the `Dmon.Hosting` namespace, so that adding the provider to an authored `Dmon.cs` is exactly a `#:package` line plus the verb appearing (no extra `using`). First-party and third-party provider packages SHALL be structurally identical.

#### Scenario: Provider package is self-contained
- **WHEN** the dependency graph of a `Dmon.Providers.<Name>` package is inspected
- **THEN** it references `Dmon.Abstractions` and its own vendor SDK only, and carries its `Use<Provider>` verb

#### Scenario: Verb appears via the Dmon.Hosting namespace
- **WHEN** an authored `Dmon.cs` (which already imports `Dmon.Hosting`) adds a `#:package Dmon.Providers.<Name>` line
- **THEN** the package's `Use<Provider>` verb is available with no additional `using` directive

### Requirement: Use verb prefix equals the registered provider name
The `Use<Provider>` verb's name prefix SHALL equal the provider's registered `ProviderName` (the model-reference contract). The verb SHALL select a model whose reference is `<ProviderName>/<model>`, so that the prefix passed to `UseModel` matches the key under which the registry stores the provider.

#### Scenario: Verb prefix matches the model-ref provider segment
- **WHEN** `UseLlamaCpp("gemma")` is called and `LlamaCppProvider.ProviderName` is `"llamacpp"`
- **THEN** the selected model reference is `"llamacpp/gemma"`, whose provider segment equals `ProviderName`

#### Scenario: Mismatched prefix is an author error
- **WHEN** a provider's `Use<Provider>` verb would select a model reference whose provider segment does not equal the registered `ProviderName`
- **THEN** the model reference cannot resolve the provider in the registry, surfacing the mismatch

### Requirement: Extension lifecycle follows the registered pipeline
A provider extension is composed at author time and routed into the registry at **build time** by DI-discovery (ADR-022 D5), not by a runtime dynamic loader. When the host is built, it SHALL enumerate `IEnumerable<IProviderExtension>` from the container and route each through the existing `IProviderRegistry.RegisterExtensionAsync`, gated by `IsApplicable()`, following the steps below:

1. The extension is registered as an `IProviderExtension` singleton via `AddProvider<T>()` (typically through a `Use<Provider>` verb) in the composition root.
2. At build, the host enumerates all registered `IProviderExtension` instances from DI.
3. For each, `IsApplicable()` is called: if `false`, a `Warning` log is emitted and the provider is not registered in `IProviderRegistry`.
4. If `true`, `IProviderRegistry.RegisterExtensionAsync(extension)` is called, which calls `CreateFactory()`, calls `ListModelsAsync()` to obtain the `DefaultModelId`, synthesises a `ProviderConfig`, and registers the factory and config.

#### Scenario: Applicable extension is routed at build time
- **WHEN** the host is built and a registered `IProviderExtension` returns `true` from `IsApplicable()`
- **THEN** the host enumerates it from DI and calls `IProviderRegistry.RegisterExtensionAsync`, registering the provider

#### Scenario: Inapplicable extension is skipped during DI-discovery
- **WHEN** the host is built and a registered `IProviderExtension` returns `false` from `IsApplicable()`
- **THEN** a `Warning` log is emitted and the provider is not registered in `IProviderRegistry`

### Requirement: Discovery and packaging constraints
A package that contributes an `IProviderExtension` SHALL ship as a granular implementation package (`Dmon.Providers.<Name>` for first-party; `<Owner>.Dmon.<Name>` recommended for third-party), referencing `Dmon.Abstractions` plus its vendor SDK. A single package MAY contribute both an `IToolExtension` (tools) and an `IProviderExtension` (a provider); both are composed independently through their respective facet verbs (`AddToolExtension`, `AddProvider`). The provider's `Use<Provider>` verb SHALL live in the `Dmon.Hosting` namespace.

#### Scenario: Provider package follows the naming convention
- **WHEN** a first-party provider package is published
- **THEN** it is named `Dmon.Providers.<Name>` and exposes a `Use<Provider>` verb in the `Dmon.Hosting` namespace

#### Scenario: Dual-purpose package composes both extension kinds
- **WHEN** a package contributes both an `IToolExtension` and an `IProviderExtension`
- **THEN** the tool extension is composed via `AddToolExtension` and the provider via `AddProvider`/`Use<Provider>`, and both are registered independently

### Requirement: Provider stop lifecycle
The provider extension lifecycle SHALL include a `StopAsync(cancellationToken)` operation that complements the attach-first `EnsureRunningAsync`. A provider that spawned a server it owns SHALL terminate that server on `StopAsync`. Providers that only attach to externally-managed servers, or that are start-only, SHALL provide a default no-op `StopAsync` so existing providers are unaffected.

#### Scenario: Owning provider stops its server
- **WHEN** `StopAsync` is called on a provider that spawned and owns a server process
- **THEN** the provider terminates that process and releases its resources

#### Scenario: Start-only provider default is a no-op
- **WHEN** `StopAsync` is called on a provider that does not own a spawned process
- **THEN** the default implementation is a no-op and no external process is affected

#### Scenario: Stop after attach leaves external server running
- **WHEN** a provider attached to an already-running external server and `StopAsync` is later called
- **THEN** the external server is left running
