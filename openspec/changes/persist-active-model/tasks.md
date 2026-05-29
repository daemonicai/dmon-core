## 1. Active-model persistence store

- [x] 1.1 Add `ActiveSelection` (record: `string Provider`, `string? Model`) and `IActiveModelStore` (`ActiveSelection? Load()`, `Task SaveAsync(ActiveSelection, CancellationToken)`) in `src/Dmon.Core` (alongside the provider/config types; mirror where `IPermissionSettings` lives).
- [x] 1.2 Implement the store backed by `state.yaml`, resolving **project scope** (`<cwd>/.dmon/state.yaml`) when a project `.dmon` directory exists, else **global** (`~/.dmon/state.yaml`). Mirror `PermissionSettingsLoader`: minimal line-based read (keys `activeProvider`, `activeModel`), atomic write (temp file + `File.Move(overwrite)`), no external YAML library. `Load()` returns null on absent/unreadable/garbage file (never throws).
- [x] 1.3 Register `IActiveModelStore` as a singleton in `src/Dmon.Core/DaemonServiceExtensions.cs`, resolving scope from `Directory.GetCurrentDirectory()` (mirror the `IPermissionSettings` registration at ~line 108).
- [x] 1.4 Tier-A tests in `test/Dmon.Core.Tests`: round-trip save→load; project scope chosen when a project `.dmon` exists; global fallback when not; absent file → `Load()` returns null; garbage file → `Load()` returns null (no throw). Use a temp working directory; do not write into the real `~/.dmon`.
- [x] 1.5 Standard gates: `make build`, `dotnet test -c Release`, `openspec validate persist-active-model --strict`; reviewer audit; commit.

## 2. Restore on startup, save on switch, commit-at-turn-start

- [ ] 2.1 Inject `IActiveModelStore` into `ProviderRegistry`. In the constructor, after adapter validation, call `Load()`; if it returns a selection whose provider is found in the providers known at construction, set `_activeIndex` to that provider and `_activeModelId` to the persisted model; otherwise keep `_activeIndex = 0`. Never throw on a stale/garbage selection (log at debug). Document the known limitation that an extension/dynamic provider registered after construction is not restorable.
- [ ] 2.2 Persist on commit without blocking I/O inside the synchronous `CommitPendingSwitch()`. Preferred seam: in `TurnHandler`, after receiving the `ProviderSwitchResult` from the commit, `await _activeModelStore.SaveAsync(new ActiveSelection(result.ProviderName, result.ModelId), ...)`. (An equivalent non-blocking seam keeping save in the registry is acceptable.)
- [ ] 2.3 In `TurnHandler.RunTurnAsync`, move the `CommitPendingSwitch()` call + `ProviderSwitchedEvent` emission (+ the new save) from the end of the turn to the **start**, before `GetCurrentAsync()` resolves the provider client. Set `ProviderSwitchedEvent.EffectiveNextTurn = false` for this between-turns commit (it applies to the turn now starting). Ensure a switch queued mid-turn is committed at the next turn's start (still effective next turn). Remove the now-redundant end-of-turn commit.
- [ ] 2.4 Tier-A tests: registry restores active provider + model from a stubbed `IActiveModelStore`; restore falls back to index 0 when the persisted provider is absent; `TurnHandler` commits the pending switch before resolving the provider client (assert the turn uses the switched-to client — extend the existing `TurnHandler` test harness / capturing client). Confirm existing `ProviderRegistry`/`TurnHandler` tests still pass (adjust any that asserted end-of-turn commit timing, if present).
- [ ] 2.5 Standard gates: `make build`, `dotnet test -c Release`, `openspec validate persist-active-model --strict`; reviewer audit; commit.

## 3. Verify + archive

- [ ] 3.1 Manual smoke (HITL — provide the recipe and wait for confirmation before ticking): `dotnet run --project src/Dmon.Terminal`; `/model` → pick Gemini + a Gemini model; send a prompt → confirm the turn uses **Gemini immediately** (no Anthropic/other-provider call, no `Model ID must be specified`). Quit and relaunch → confirm the active provider/model is still the Gemini selection (restored from `state.yaml`). Inspect the written `.dmon/state.yaml`.
- [ ] 3.2 Standard gates: build, test, `openspec validate persist-active-model --strict`.
- [ ] 3.3 Propose `/opsx:archive persist-active-model` and wait for user confirmation. Do not archive automatically.
