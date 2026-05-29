## Context

`TurnHandler.RunTurnAsync` (`src/Dmon.Core/Rpc/TurnHandler.cs`) builds the per-turn chat options as `ChatOptions options = new();` (line ~221) and only ever sets `Tools` and the thinking parameters (`ApplyThinkingToOptions`). It never sets `options.ModelId`. The provider pipeline is rebuilt each turn and the `IChatClient` from the active provider factory is invoked via `pipeline.GetStreamingResponseAsync(_history, options, …)`.

Because `options.ModelId` is null, the model resolution falls entirely to each provider's client default, baked in at factory construction:

- `OpenAiProviderFactory`: `new OpenAI.Chat.ChatClient(modelId, …)`.
- `GeminiProviderFactory`: `GeminiClientOptions { ModelId = … }`.
- `AnthropicProviderFactory`: `AnthropicClient(...).AsIChatClient(modelId)` (after `adopt-official-anthropic-sdk`).

`GeminiDotnet.Extensions.AI 0.25.0` does not expose its constructor-supplied `ModelId` as the M.E.AI default the request guard inspects, so a Gemini turn throws `Model ID must be specified either in ChatOptions or as the default for the client.` The same class of failure bit Anthropic until its factory was made to bake the model.

The active model id is already tracked: `IProviderRegistry.GetCurrentModelId()` returns the switched-to model (or null), and `GetCurrentConfig().DefaultModelId` is the configured default. `TurnHandler` holds the registry as `_providers`.

## Goals / Non-Goals

**Goals:**

- The core sets `ChatOptions.ModelId` to the active model on every provider call, so model resolution does not depend on provider-specific baked defaults.
- Gemini turns complete; OpenAI and Anthropic turns are unaffected.
- The active model reflects an in-session model switch (`GetCurrentModelId()`), not just the static config default.

**Non-Goals:**

- No provider-factory changes; the baked defaults stay as a fallback.
- Not changing thinking/tool option handling.
- Not reconciling the telemetry `model` variable (line ~192) — out of scope, though noted below.

## Decisions

### 1. Set `options.ModelId` from the active model, registry-first

In `RunTurnAsync`, where `options` is constructed, set:

```csharp
string? activeModelId = _providers.GetCurrentModelId() ?? _providers.GetCurrentConfig().DefaultModelId;
if (!string.IsNullOrWhiteSpace(activeModelId))
    options.ModelId = activeModelId;
```

Registry-first (`GetCurrentModelId()`) so an in-session `/model` switch is honoured; fall back to the config default. Guard on non-empty so an unconfigured model does not set `ModelId = ""` (which would mask the M.E.AI "must be specified" guard with a worse downstream error). The placement is alongside the existing `options` setup (after `new ChatOptions()`, near the `Tools`/thinking assignment), inside the per-turn `while` loop so a mid-session switch takes effect on the next turn.

Rejected: setting `ModelId` only when a provider needs it (provider-specific branching) — fragile; specifying the model explicitly is correct for all M.E.AI clients.

### 2. Leave the baked factory defaults in place

Keep OpenAI/Gemini/Anthropic baking their model at construction. With an explicit `options.ModelId` they become a harmless fallback. Removing them is unnecessary churn and would widen scope into the provider layer.

## Risks / Trade-offs

- **Risk: the explicit `ModelId` differs from the baked default and a provider mishandles the override.** *Mitigation:* the active model id is the same value the factory baked (both derive from `config.DefaultModelId`, or the registry's switched value which is the user's intent). Manual smoke on each provider confirms.
- **Risk: `GetCurrentModelId()` is null early (before any switch) and the config default is also empty.** *Mitigation:* the non-empty guard leaves `ModelId` unset, preserving today's behaviour (the baked default still applies); the wizard requires a model before a config persists, so empty is not reachable in practice.
- **Trade-off: telemetry `model` (line ~192) still reads only `GetCurrentConfig().DefaultModelId`, so it can disagree with a switched model.** Pre-existing; explicitly out of scope here. Noted for a possible follow-up.

## Migration Plan

Single capability, one task group: edit `RunTurnAsync`'s options setup, gates (build, test, validate), reviewer audit, commit. Then a manual smoke group (Gemini + OpenAI/Anthropic regression) and archive. Branch `change/set-turn-model-id` off `main`.

Rollback: revert the `TurnHandler.cs` edit; Gemini returns to the baked-default failure.

## Open Questions

None. The active-model source and the single edit site are established.
