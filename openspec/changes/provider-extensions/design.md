# Design: Provider Extensions

## Overview

This change introduces `IProviderExtension` as a second extension kind. When a NuGet assembly is loaded by the extension pipeline, it may implement `IProviderExtension` (instead of, or in addition to, `IDmonExtension`). The loader detects the interface, runs the applicability check, and — if applicable — registers the provider with `IProviderRegistry` so it is immediately available for `SetProvider` / `CycleProvider`.

## Affected components

| Component | Change |
|-----------|--------|
| `Dmon.Abstractions` | New: `IProviderExtension`, `ModelInfo` |
| `Dmon.Core/Extensions/NuGetExtensionLoader` | Discovers `IProviderExtension` types alongside `IDmonExtension` |
| `Dmon.Core/Extensions/ExtensionLoadResult` | New optional field: `ProviderExtension` |
| `Dmon.Core/Extensions/ExtensionService` | Routes `ProviderExtension` to `IProviderRegistry`; relaxes tools-required invariant |
| `Dmon.Core/Providers/IProviderRegistry` | New: `RegisterExtensionAsync` |
| `Dmon.Core/Providers/ProviderRegistry` | Implements runtime provider registration; synthesizes `ProviderConfig` |
| `Dmon.Protocol/Events` | `ExtensionLoadedEvent` gains optional `ProviderName` field |

## New types in `Dmon.Abstractions`

### `ModelInfo`

```csharp
namespace Dmon.Abstractions.Providers;

public sealed record ModelInfo
{
    public required string Id { get; init; }
    public required ChatClientCapabilities Capabilities { get; init; }
}
```

### `IProviderExtension`

```csharp
namespace Dmon.Abstractions.Providers;

public interface IProviderExtension
{
    /// <summary>Human-readable provider name, e.g. "oMLX".</summary>
    string ProviderName { get; }

    /// <summary>
    /// Returns true if this provider can run on the current platform and hardware.
    /// Called once at extension load time. If false, a Warning is logged and the
    /// provider is not registered. The extension pipeline approval is still stored.
    /// </summary>
    bool IsApplicable();

    /// <summary>
    /// Returns true if the inference server is currently reachable.
    /// Implementations should verify server identity, not just port reachability.
    /// </summary>
    Task<bool> IsRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the server if not running. Only called after an ADR-006
    /// confirmation prompt initiated by the daemon.
    /// </summary>
    Task EnsureRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns models currently available from the running server.
    /// </summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an IProviderFactory configured for this runner.
    /// Called after IsApplicable() returns true; the factory is registered
    /// with ProviderRegistry.
    /// </summary>
    IProviderFactory CreateFactory();
}
```

## Extension loader changes

### `ExtensionLoadResult`

Add an optional field alongside the existing `Extension` and `Tools`:

```csharp
public IProviderExtension? ProviderExtension { get; init; }
```

### `NuGetExtensionLoader.DiscoverExtensions`

The existing method only looks for `IDmonExtension`. The updated version scans for both:

```csharp
private static (List<IDmonExtension> tools, List<IProviderExtension> providers)
    DiscoverAll(Assembly assembly, IServiceProvider serviceProvider)
```

A type implementing both interfaces is valid (though unusual). Construction follows the same logic: prefer `(IServiceProvider)` constructor, fall back to parameterless.

### `ExtensionService.LoadAsync`

Current hard invariant: `result.Tools.Count == 0` → error. This is relaxed:

- If the result has tools but no provider → existing behaviour (register tools).
- If the result has a provider but no tools → call `IsApplicable()` and register the provider if applicable; this is not an error.
- If the result has both → register tools and provider.
- If the result has neither → error (unchanged).

When `IsApplicable()` returns false:
```
_logger.LogWarning(
    "Provider extension '{Name}' is not applicable on this platform and will not be activated.",
    result.ProviderExtension.ProviderName);
```
The `ExtensionLoadedEvent` is still emitted (the extension was loaded), but with `ProviderName = null`.

When `IsApplicable()` returns true, `RegisterExtensionAsync` is called on `IProviderRegistry`.

`ExtensionService` gains a nullable `IProviderRegistry?` dependency injected via constructor. If `IProviderRegistry` is not registered in DI (unit-test hosts), provider extensions fall back to the "not applicable" path with a Warning.

## Registry changes

### `IProviderRegistry`

```csharp
/// <summary>
/// Registers a provider extension at runtime. Synthesises a ProviderConfig
/// from the extension and adds it to the set of available providers.
/// The provider is immediately selectable via SetProvider(extension.ProviderName).
/// </summary>
Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default);
```

### `ProviderRegistry.RegisterExtensionAsync`

The registry currently holds a fixed `IReadOnlyList<ProviderConfig>` built at DI time. Extension providers are stored in a separate `List<ProviderConfig>` (mutable) alongside their factories. `GetAll()` returns both lists concatenated.

Registration flow:

1. Call `extension.CreateFactory()` → `IProviderFactory factory`
2. Call `extension.ListModelsAsync()` → pick first model as `DefaultModelId` (empty string if list is empty)
3. Synthesise `ProviderConfig`:
   ```csharp
   new ProviderConfig
   {
       Name = extension.ProviderName,
       Adapter = factory.AdapterName,
       DefaultModelId = models.FirstOrDefault()?.Id,
       Auth = new ProviderAuthConfig { Type = "none" }
   }
   ```
4. Store factory under `factory.AdapterName` (same dictionary as built-in factories).
5. Append config to the extension-provider list.

If a provider with the same `Name` is already registered (extension reloaded), the existing entry is replaced.

## Event changes

`ExtensionLoadedEvent` gains:

```csharp
/// <summary>
/// Non-null when the extension registered a provider.
/// Null when the extension was inapplicable or had no provider.
/// </summary>
public string? ProviderName { get; init; }
```

## ADR

ADR-007 documents the local-runner lifecycle decisions (IsApplicable semantics, EnsureRunningAsync / ADR-006 interaction, approval persistence). It is written as part of Group 1.
