## Context

The terminal has a hardcoded `/model` → `ModelSetCommand` path that was never fully implemented. The provider registry tracks a pending model ID during a turn but clears it during `CommitPendingSwitch()` without storing the committed value — meaning the runtime-active model ID is only known to the terminal's local `_modelName` field. `ModelListResultEvent` currently uses `DefaultModelId` for `ActiveModelId`, so it cannot correctly pre-select the live model.

The `/model` command should be a two-step interactive flow: choose provider (immediate, from registry), then choose model (live-fetched from the chosen provider). This requires a new round-trip: `ModelModelsCommand { Provider }` → `ModelModelsResultEvent { Provider, Models, ActiveModelId }`.

## Goals / Non-Goals

**Goals:**
- Interactive `/model` picker: arrow-key select provider → arrow-key select model (live-fetched)
- Pre-select active provider; pre-select active model when same provider is re-chosen
- Show loading state in the terminal between provider selection and model list arrival
- Cancel with Escape (either step) without sending any command
- Fix `ProviderRegistry` so the committed active model ID survives `CommitPendingSwitch()`
- Fix `ModelListResultEvent.ActiveModelId` to use runtime state

**Non-Goals:**
- `/model <provider> <model>` inline argument form (separate ergonomics task)
- Model capability filtering in the picker UI
- Saving the selected model back to `config.yaml`

## Decisions

### D1: Two round-trips (provider list → model list)

**Decision:** Send `ModelListCommand` first to get configured providers (instant, in-process), then `ModelModelsCommand { Provider }` after the user picks a provider to fetch models live.

**Rationale:** A combined "all providers + all models" response would require parallel live fetches from all providers simultaneously, with no way to show incremental results. The two-step design gives instant first-step display, then a single targeted fetch with a clean loading state.

**Alternative considered:** One-shot combined response — rejected because it requires eagerly fetching every provider's model list before the user has expressed intent, and would be slower overall.

### D2: `ProviderRegistry._activeModelId` field

**Decision:** Add `string? _activeModelId` to `ProviderRegistry`, updated atomically inside `CommitPendingSwitch()` alongside `_activeIndex`. Expose via `IProviderRegistry.GetCurrentModelId()`.

**Rationale:** After `CommitPendingSwitch()` clears `_pendingModelId`, no in-process component knows the committed model. The terminal's `_modelName` is the only source of truth — but it's in the wrong process. The registry is the canonical owner; the field costs nothing.

**Alternative considered:** Source `ActiveModelId` from `ModelListResultEvent.ActiveModelId = config.DefaultModelId` — rejected because it diverges from runtime truth after any `/model` switch.

### D3: Terminal input locked during picker interaction

**Decision:** The terminal's `InputReader` is suspended (same lock used by the wizard) while either picker is open. User keystrokes are routed to the picker, not the main input loop.

**Rationale:** Re-using the existing lock mechanism avoids a parallel input path. The picker is modal by nature.

**Alternative considered:** Non-modal picker with the picker rendered above the prompt — rejected because it requires cursor management beyond the current renderer's scope.

### D4: `ModelModelsHandler` uses factory's `GetAvailableModelsAsync`

**Decision:** `ModelModelsHandler` resolves credentials for the named provider from config, then calls `factory.GetAvailableModelsAsync(apiKey, ct)` with a 5-second timeout. Returns model IDs only (strings), not full `ModelInfo`.

**Rationale:** Consistent with the existing `ModelListHandler` pattern. The 5-second timeout matches the convention established in the provider-model-listing spec.

### D5: Pre-selection logic

**Decision:**
- Provider picker: pre-select index where `provider.Name == event.ActiveProvider` (case-insensitive)
- Model picker: pre-select index where `model == event.ActiveModelId` only if the user selected the same provider that is currently active; otherwise index 0

**Rationale:** If the user switches to a different provider, pre-selecting the old model ID is meaningless (it won't exist in the other provider's list). Default to first model for cross-provider switches.

## Risks / Trade-offs

- **Stale model list**: `GetAvailableModelsAsync` fetches live — if the provider's model list changes between terminal sessions, users see fresh data (desired). But if the provider is temporarily unreachable, the picker shows an error or empty list. Mitigation: fall back to static list (already implemented in each factory).
- **Escape handling**: The terminal's `InputReader` currently doesn't route individual keypresses. Escape must be handled by polling `Console.KeyAvailable` inside the picker loop, not via the async line-read path. Mitigation: picker owns its own read loop while input is locked.
- **Registry `GetCurrentModelId()` returns null on first launch**: Before any commit, `_activeModelId` is null; fall back to `config.DefaultModelId` in `ModelListResultEvent`. This is the same behaviour as today.
