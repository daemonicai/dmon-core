## Why

A turn against the Gemini provider fails with `Model ID must be specified either in ChatOptions or as the default for the client.` The agent core's turn loop (`TurnHandler.RunTurnAsync`) builds `ChatOptions options = new()` and never sets `options.ModelId`, so every provider depends on a model baked into its `IChatClient` at construction. `GeminiDotnet.Extensions.AI`'s adapter does not surface its constructor-supplied model as the M.E.AI default the request guard checks, so the turn throws.

Relying on each provider's baked-in default is fragile and provider-specific. The robust fix is to specify the active model explicitly on every provider call, which the active-model abstraction already tracks. This fixes Gemini and hardens all providers (the factory-baked defaults remain a harmless fallback).

## What Changes

- In `src/Dmon.Core/Rpc/TurnHandler.RunTurnAsync`, set `ChatOptions.ModelId` to the active model id — `_providers.GetCurrentModelId() ?? _providers.GetCurrentConfig().DefaultModelId` — on the per-turn `options`, so the provider call always carries an explicit model. Only set it when non-empty.
- No provider-factory changes: OpenAI, Gemini, and Anthropic continue to bake a default model; that default now acts as a fallback behind the explicit per-turn `ModelId`.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `agent-core` — the "Turn execution loop" requirement gains the guarantee that the core sets `ChatOptions.ModelId` to the active model on each provider call.

## Impact

- **Code:** `src/Dmon.Core/Rpc/TurnHandler.cs` only (the `RunTurnAsync` options setup). No other files.
- **Runtime:** unblocks turns on providers whose M.E.AI adapter requires an explicit `ChatOptions.ModelId` (Gemini today); no behaviour change for providers that already worked via a baked default.
- **Verification:** no offline unit test exercises a real provider turn; the fix is verified by a manual smoke against the Gemini provider (and a regression check that OpenAI/Anthropic still work) plus the existing suite for non-regression.
- **Relationship:** independent of `adopt-official-anthropic-sdk` (that change fixes the Anthropic ABI crash + factory; this fixes the turn loop's model specification). Both are needed for a fully working multi-provider turn.
