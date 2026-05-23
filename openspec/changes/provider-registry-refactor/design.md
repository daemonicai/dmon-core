## Context

`ProviderRegistry` currently imports `OpenAI`, `Anthropic.SDK`, and `GeminiDotnet` directly and contains a three-arm `switch` on `config.Adapter` inside `CreateClientAsync`. Adding a fourth provider requires modifying `Daemon.Core`. Provider capability metadata (`ToolCalling`, `Reasoning`) is read from static config bools that can be wrong and can't be overridden at runtime. `CommitPendingSwitch` returns `ProviderSwitchedEvent` (a `Daemon.Protocol` type), giving `IProviderRegistry` an upward dependency on the protocol layer. `SetProvider(name, modelId?)` conflates provider and model selection into one operation.

## Goals / Non-Goals

**Goals:**
- `Daemon.Core` has zero direct LLM SDK dependencies after this change
- Adding a new LLM provider requires adding a file in `Daemon.Providers` and registering it — no Core modification
- Capability metadata is owned and maintained by each factory, with per-model-id defaults
- `IChatClient` instances returned by factories expose capabilities via `GetService(typeof(ChatClientCapabilities))`, following the M.E.AI service-discovery pattern
- `IProviderRegistry` carries no `Daemon.Protocol` dependency
- Provider switching and model switching are independently queueable operations

**Non-Goals:**
- Runtime capability probing against live API endpoints (deferred; static per-model defaults cover V1)
- Third-party `IProviderFactory` implementations loaded via the extension mechanism (adapters must be present at startup; extension model is session-scoped)
- Streaming capability detection (tool-call streaming, thinking block streaming) — covered by static defaults for now

## Decisions

### D1: `IProviderFactory` defined in `Daemon.Core`, implemented in `Daemon.Providers`

```csharp
// Daemon.Core/Providers/IProviderFactory.cs
public interface IProviderFactory
{
    string AdapterName { get; }
    ChatClientCapabilities GetCapabilities(string modelId);
    ValueTask<IChatClient> CreateAsync(
        ProviderConfig config,
        string? apiKey,
        CancellationToken cancellationToken = default);
}
```

`IProviderFactory` lives in `Daemon.Core` so `ProviderRegistry` can depend on it without a circular reference. Implementations live in `Daemon.Providers`. `Daemon.Core` references `Daemon.Providers` for startup registration only (same pattern as `Daemon.Core → Daemon.BuiltinTools`).

**Alternative considered**: `IProviderFactory` in a shared `Daemon.Abstractions` project. Rejected: overkill for V1; the one-way `Core → Providers` reference is sufficient and doesn't create cycles.

### D2: `ChatClientCapabilities` is a class, retrieved via `GetService`

```csharp
// Daemon.Core/Providers/ChatClientCapabilities.cs
public sealed class ChatClientCapabilities
{
    public bool SupportsToolCalling { get; init; }
    public bool SupportsReasoning { get; init; }
    public int ContextWindow { get; init; }
    public int MaxTokens { get; init; }
}
```

Each factory's `CreateAsync` wraps the created `IChatClient` in a thin `CapabilitiesDecorator` that returns the capabilities instance from `GetService(typeof(ChatClientCapabilities))`. This mirrors how `ChatClientMetadata` works in `Microsoft.Extensions.AI`. Callers that hold an `IChatClient` can probe it without knowing which factory created it.

`ProviderRegistry.CurrentSupportsToolCalling` uses the hybrid path:

```csharp
// If client already exists, probe it; otherwise ask the factory.
(ChatClientCapabilities?)(_activeClient?.GetService(typeof(ChatClientCapabilities)))
    ?? _factories[config.Adapter].GetCapabilities(config.DefaultModelId ?? string.Empty)
```

**Alternative considered**: Capability properties directly on `IProviderFactory` without client decoration. Rejected: loses the M.E.AI-native access pattern; callers holding only an `IChatClient` cannot probe capabilities.

**Alternative considered**: Separate `ICapabilityProvider` interface. Rejected: unnecessary indirection; `IProviderFactory` is already the right scope.

### D3: Per-model-id capability defaults inside each factory

Each factory maintains an internal lookup (switch expression or dictionary) from known model IDs to `ChatClientCapabilities`. Unknown model IDs fall back to a conservative default (`SupportsToolCalling = false`, `SupportsReasoning = false`). Config-level `capabilities` section is removed; there is no config override path in V1 (factory defaults are authoritative).

```csharp
// AnthropicProviderFactory.cs
public ChatClientCapabilities GetCapabilities(string modelId) => modelId switch
{
    var m when m.StartsWith("claude-3", StringComparison.OrdinalIgnoreCase)
        => new() { SupportsToolCalling = true, SupportsReasoning = false, ... },
    var m when m.StartsWith("claude-opus-4", StringComparison.OrdinalIgnoreCase)
             || m.StartsWith("claude-sonnet-4", StringComparison.OrdinalIgnoreCase)
        => new() { SupportsToolCalling = true, SupportsReasoning = true, ... },
    _ => new() { SupportsToolCalling = false, SupportsReasoning = false }
};
```

**Trade-off**: The lookup table goes stale as new models are released. Accepted for V1 — the conservative unknown-model default means new models prompt rather than silently misbehave. A config-override path can be added in V1.5.

### D4: `CommitPendingSwitch` returns `ProviderSwitchResult`

```csharp
// Daemon.Core/Providers/ProviderSwitchResult.cs
public sealed record ProviderSwitchResult(string ProviderName, string ModelId);
```

`IProviderRegistry.CommitPendingSwitch` returns `ProviderSwitchResult?`. `TurnHandler` maps it to `ProviderSwitchedEvent` before emitting. This removes the `Daemon.Protocol` reference from `IProviderRegistry` and `ProviderRegistry`.

**Alternative considered**: Keep returning `ProviderSwitchedEvent`. Rejected: upward protocol dependency on an interface that should know nothing about the event wire format.

### D5: `SetProvider` and `SetModel` as separate queued operations

```csharp
void SetProvider(string name);
void SetModel(string modelId);
```

Both enqueue a pending change effective at the next `CommitPendingSwitch` call (between turns). `SetModel` validates the model ID at enqueue time against the factory's known model IDs for the current or pending provider; if the model is unrecognised, it is accepted with a warning log (conservative: unknown model IDs are not blocked, since the factory already falls back to safe capability defaults). The combined `SetProvider(name, modelId?)` overload is removed.

**Alternative considered**: Validate model IDs strictly and throw on unknown. Rejected: new models ship faster than factory tables update; a strict validator would break on every model release.

### D6: `CapabilitiesDecorator` is a private inner class per factory

Each factory's `CreateAsync` wraps the raw SDK client:

```csharp
private sealed class CapabilitiesDecorator(IChatClient inner, ChatClientCapabilities caps) : IChatClient
{
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientCapabilities) ? caps : inner.GetService(serviceType, serviceKey);

    // Forward all other members to inner.
    public Task<ChatResponse> GetResponseAsync(...) => inner.GetResponseAsync(...);
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(...) => inner.GetStreamingResponseAsync(...);
    public void Dispose() => inner.Dispose();
}
```

Private inner class per factory avoids a shared decorator type in Core that would need to know about `ChatClientCapabilities` before the type is defined — and keeps each factory self-contained.

## Risks / Trade-offs

- **Capability table staleness** → Mitigated: unknown model IDs fall back to `SupportsToolCalling = false`, so the system prompts rather than silently omitting tools. Worst case: user sees a permission prompt for tool calls on a model that should auto-allow.
- **`Daemon.Core → Daemon.Providers` reference** → Same pattern as `Core → BuiltinTools`. Fine for V1; if Core must ever be published without Providers, extract `IProviderFactory` to a shared abstractions package.
- **Breaking `SetProvider` signature** → Only `TurnHandler` (via `IModelHandler`) and `CommandDispatcher` call `SetProvider`; both are internal. `model.set` RPC command will need its handler updated to call `SetProvider` + `SetModel` separately.
- **`CapabilitiesDecorator` forwarding boilerplate** → `IChatClient` has only three members; boilerplate is minimal and unlikely to grow.

## Open Questions

*(none — resolved in explore session)*
