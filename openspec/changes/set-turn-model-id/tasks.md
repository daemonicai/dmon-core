## 1. Set ChatOptions.ModelId in the turn loop

- [x] 1.1 In `src/Dmon.Core/Rpc/TurnHandler.RunTurnAsync`, where the per-turn `ChatOptions options = new();` is built (inside the `while` loop, alongside the `Tools`/thinking setup), set the active model id:
  ```csharp
  string? activeModelId = _providers.GetCurrentModelId() ?? _providers.GetCurrentConfig().DefaultModelId;
  if (!string.IsNullOrWhiteSpace(activeModelId))
      options.ModelId = activeModelId;
  ```
  Registry-first so an in-session model switch is honoured; guard on non-empty so an unconfigured model leaves `ModelId` unset (preserving the provider's baked default).
- [x] 1.2 Do not change any provider factory, the thinking/tool option handling, or the telemetry `model` variable (line ~192). Scope is the `options` setup only.
- [x] 1.3 If a tier-A test is feasible without a live provider (e.g. a fake `IProviderRegistry` + a capturing `IChatClient` asserting the received `ChatOptions.ModelId`), add one under `test/Dmon.Core.Tests`. If the turn loop is not unit-testable in isolation without significant scaffolding, note that and rely on the manual smoke — do not build elaborate test infrastructure for this one-line fix.
- [x] 1.4 Standard gates: `make build` clean (no warnings under `TreatWarningsAsErrors`), `make test` (or `dotnet test -c Release`) green, `openspec validate set-turn-model-id --strict`; reviewer audit; commit.

## 2. Verify + archive

- [ ] 2.1 Manual smoke (HITL — provide the recipe and wait for confirmation before ticking): with the Gemini provider active and `GEMINI_API_KEY` set, run `dotnet run --project src/Dmon.Terminal`, submit a prompt, and confirm the turn completes with no `Model ID must be specified` error. Regression check: confirm an OpenAI and/or Anthropic turn still completes (use the `adopt-official-anthropic-sdk` branch or a merge of both for the Anthropic check).
- [ ] 2.2 Standard gates: build, test, `openspec validate set-turn-model-id --strict`.
- [ ] 2.3 Propose `/opsx:archive set-turn-model-id` and wait for user confirmation. Do not archive automatically.
