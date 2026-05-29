## Why

Two related defects make provider/model selection unreliable:

1. **The selection is never persisted.** `ProviderRegistry` tracks the active provider index and model id only in memory; nothing writes them back to disk. On restart, dmon reverts to the first configured provider (index 0) and its default model — the user's last choice is lost.
2. **A selection takes effect one turn late.** `TurnHandler` commits the pending switch at the *end* of a turn (`CommitPendingSwitch` after the LLM call), so picking a provider/model and then sending a message runs that message against the *previous* provider. In practice, choosing Gemini and typing a prompt hit the startup-default Anthropic provider instead.

Together these mean the active model neither sticks within a session nor survives a restart.

## What Changes

- **Persist the active selection.** Add an `IActiveModelStore` that reads/writes the active provider name + model id to `.dmon/state.yaml` — **project scope** (`<cwd>/.dmon/`) when a project `.dmon` directory exists, falling back to **global** (`~/.dmon/`). Mirrors the existing `PermissionSettingsLoader` atomic temp-file-move write; no external YAML dependency. A dedicated `state.yaml` is used rather than `config.yaml`/`settings.yaml` to avoid clobbering hand-authored config or the permissions block.
- **Restore on startup.** `ProviderRegistry` initialises its active provider index and `_activeModelId` from the persisted selection when present and still configured; otherwise it keeps today's default (index 0).
- **Save on switch.** When a switch is committed, the new active provider + model is written to the store.
- **Fix switch timing (folded in).** `TurnHandler` SHALL commit any pending switch at the **start** of a turn (before the provider client is resolved), so a selection made between turns takes effect on that turn. A switch issued *during* a turn still defers to the next turn (unchanged).

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `provider-registry` — adds persistence: restore the active provider/model from disk at startup and save it when a switch is committed.
- `agent-core` — the turn loop commits a pending provider/model switch before resolving the provider client, so a between-turns selection is effective on the next turn (not one turn late).

## Impact

- **Code:** new `IActiveModelStore` + implementation (`Dmon.Core`); `ProviderRegistry` (restore in ctor/init, save on commit); `TurnHandler` (move commit to turn start); DI wiring in `DaemonServiceExtensions`.
- **Files on disk:** introduces `.dmon/state.yaml` (project or global). Documented; safe atomic writes.
- **Behaviour:** the selected provider/model is used immediately on the next turn and remembered across restarts. No change when no selection has been made (default provider/model as today).
- **Verification:** tier-A tests for the store (round-trip, project-vs-global resolution, absent file) and the registry (restore, save-on-commit); a turn-loop test asserting the pending switch is committed before the provider client is resolved. Manual smoke: pick Gemini, send a prompt (uses Gemini immediately), restart (still Gemini).
- **Out of scope / follow-ups noted:** the `provider-registry` standing spec still says `.daemon/config.yaml` and "Anthropic.SDK community package" — both stale (code uses `.dmon/`; the Anthropic package was replaced by `adopt-official-anthropic-sdk`). Not corrected here.
