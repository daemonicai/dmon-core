## Context

`ProviderRegistry` (`src/Dmon.Core/Providers/ProviderRegistry.cs`) holds the active provider as `_activeIndex` (default 0) and the active model as `_activeModelId`, both in memory. `SetProvider`/`SetModel` queue `_pendingIndex`/`_pendingModelId`; `CommitPendingSwitch()` applies them, disposes the old `IChatClient`, and returns a `ProviderSwitchResult?`. `TurnHandler.RunTurnAsync` calls `CommitPendingSwitch()` at the **end** of the turn (`TurnHandler.cs:341`) and emits `ProviderSwitchedEvent { EffectiveNextTurn = true }`.

Config is read-only: `Program.cs` adds `<cwd>/.dmon/config.yaml` then `~/.dmon/config.yaml` via `AddYamlFile`. The only existing write-back pattern is `PermissionSettingsLoader`, which loads/saves `.dmon/settings.yaml` (project) or `~/.dmon/settings.yaml` (global) using a minimal line-based serialiser and an atomic temp-file move — no external YAML library (the project avoids one for writes). DI is wired in `DaemonServiceExtensions.cs` (registry at line 43; `IPermissionSettings` resolved project-scope at 108).

Two consequences: the active selection is lost on restart, and (because the commit runs after the LLM call) a selection made between turns takes effect one turn late — the reported "picked Gemini, but the turn used Anthropic."

## Goals / Non-Goals

**Goals:**

- Persist the active provider name + model id and restore it at startup (project scope, global fallback).
- A selection made between turns is used on the very next turn.
- No external YAML dependency; safe atomic writes; no clobbering of hand-authored `config.yaml` or the permissions `settings.yaml`.

**Non-Goals:**

- Not writing into `config.yaml` (hand-authored, comment-bearing, round-trip-unsafe without a YAML library).
- Not changing the terminal `model-switcher` picker UI (it already pre-selects `ActiveProvider`/`ActiveModelId`).
- Not changing mid-turn switch semantics (a switch issued during a turn still defers to the next turn).
- Not correcting the stale `.daemon/` / "Anthropic.SDK" wording in the standing `provider-registry` spec (separate follow-up).

## Decisions

### 1. Dedicated `state.yaml`, project-then-global

Persist to `.dmon/state.yaml`:

```yaml
activeProvider: gemini
activeModel: gemini-2.0-flash-lite
```

Scope resolution: use the project file (`<cwd>/.dmon/state.yaml`) when a project `.dmon` directory exists; otherwise the global file (`~/.dmon/state.yaml`). This mirrors how a project `.dmon` already overrides global config, and keeps per-project model choices.

A dedicated file (not `config.yaml`, not `settings.yaml`) avoids two minimal writers fighting over one file and keeps runtime state separate from declarative config. Reading uses `IConfiguration`-style or a tiny parser; writing mirrors `PermissionSettingsLoader.SaveAsync` (temp file + `File.Move(overwrite)`).

### 2. `IActiveModelStore` abstraction

```csharp
public sealed record ActiveSelection(string Provider, string? Model);

public interface IActiveModelStore
{
    ActiveSelection? Load();
    Task SaveAsync(ActiveSelection selection, CancellationToken cancellationToken = default);
}
```

Implementation resolves the file path (project-then-global) once at construction, like `PermissionSettingsLoader.LoadProject`/`LoadGlobal`. Registered as a singleton in `DaemonServiceExtensions`.

### 3. Restore at startup in `ProviderRegistry`

Inject `IActiveModelStore` into `ProviderRegistry`. After the existing adapter validation in the constructor, if `store.Load()` returns a selection whose provider is configured, set `_activeIndex` to that provider's index and `_activeModelId` to the persisted model. If the persisted provider is no longer configured (or the file is absent), keep `_activeIndex = 0` and log at debug. Restoration must not throw on a stale/garbage file — treat load failure as "no selection."

Note `GetAll()` ordering: extension/dynamic providers are appended after built-ins, and may not be registered at construction time. Restoration resolves against the providers known at construction; a persisted *extension* provider that registers later is a known limitation (document it; do not block on it).

### 4. Save on commit

When a switch is committed, persist the new active provider + model. `CommitPendingSwitch()` is synchronous and returns the `ProviderSwitchResult`; file I/O should not block inside it. Prefer persisting from the async commit path: have `TurnHandler` (which already receives the `ProviderSwitchResult`) call `await _activeModelStore.SaveAsync(new ActiveSelection(result.ProviderName, result.ModelId))`. The store is the single owner of the file; the registry restores from it, the turn loop saves through it. (If the worker finds a cleaner seam to keep both load and save in the registry without sync-over-async, that is acceptable provided no blocking I/O occurs inside `CommitPendingSwitch`.)

### 5. Commit the pending switch at the start of the turn

Move the `CommitPendingSwitch()` call (and its `ProviderSwitchedEvent` emission + the new persistence save) from the end of `RunTurnAsync` to the **start**, before `GetCurrentAsync()` resolves the provider client (currently `TurnHandler.cs:214`). Effect:

- A switch queued **between** turns is committed at the start of the next turn → that turn uses the new provider/model. Fixes the reported bug.
- A switch queued **during** a turn (the loop is mid-flight) is committed at the start of the **following** turn → still "effective next turn," preserving the mid-turn-defers contract.

The `ProviderSwitchedEvent.EffectiveNextTurn` flag becomes `false` for the between-turns case (it is now effective this turn). Keep emitting the event so the host can update its model indicator; set the flag to reflect reality.

Rejected: committing at both start and end — redundant and risks double-emitting the event.

## Risks / Trade-offs

- **Risk: moving the commit changes `ProviderSwitchedEvent` timing/flag and could surprise the terminal host.** *Mitigation:* the host treats the event as "active model changed"; verify the model indicator still updates. The `model-switcher` picker pre-selection reads `ActiveProvider`/`ActiveModelId`, which now reflect the committed (and persisted) state — more correct, not less.
- **Risk: persisted provider no longer configured (config edited, extension not loaded).** *Mitigation:* restoration falls back to default index 0 and logs; never throws.
- **Risk: concurrent writers / partial writes.** *Mitigation:* atomic temp-file + `File.Move(overwrite)`, as `PermissionSettingsLoader` does.
- **Trade-off: a dedicated `state.yaml` rather than the user-imagined "config."** Chosen for write-safety; documented in the proposal so it can be redirected.

## Migration Plan

Branch `change/persist-active-model` off `main`. Task groups:
1. `IActiveModelStore` + implementation + DI wiring + store tests.
2. `ProviderRegistry` restore-on-startup + save-on-commit seam; `TurnHandler` commit-at-turn-start; registry/turn tests.
3. Gates + manual smoke (pick Gemini → used immediately; restart → still Gemini) + archive.

Rollback: revert the three commits; selection returns to in-memory-only + end-of-turn commit.

## Open Questions

None blocking. The `state.yaml` filename and the save-from-TurnHandler seam are recorded above; the worker may choose an equivalent non-blocking seam.
