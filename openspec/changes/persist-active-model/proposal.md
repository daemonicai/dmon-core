## Why

Three related gaps make provider/model selection unreliable and awkward to reference:

1. **The selection is never persisted.** `ProviderRegistry` tracks the active provider index and model id only in memory; on restart dmon reverts to the first configured provider and its default model — the user's last choice is lost.
2. **A selection takes effect one turn late.** `TurnHandler` commits the pending switch at the *end* of a turn, so picking a provider/model and then sending a message runs against the *previous* provider (observed: choosing Gemini, the next prompt hit the startup-default Anthropic).
3. **There is no canonical way to name a model.** Provider and model are passed around as two separate fields, which is clumsy for the model-handling work to come.

## What Changes

- **Introduce a canonical `ModelRef`** value type — a single string `{provider}/{model}`. The provider key is everything before the **first** `/`; everything after (including further slashes) is the provider-owned model id, passed through unmolested (`ollama/deepseek/deepseek-v4-pro` → provider `ollama`, model `deepseek/deepseek-v4-pro`). `ModelRef` lives in `Dmon.Abstractions` as a reusable primitive. It is the canonical internal representation; the RPC `ModelSetCommand{Provider,ModelId}` stays two-field at the protocol edge.
- **Persist the active selection as `config.local.yaml`** — a git-ignored, app-managed **IConfiguration layer**, project scope (`./.dmon/config.local.yaml`), holding a single top-level `activeModel: {provider}/{model}`. Because it is a config layer, the active model is **read for free through `IConfiguration`** (no custom read parser); only the write path is app code.
- **Fix the config-layer precedence.** Order the YAML layers lowest→highest: `~/.dmon/config.yaml` < `./.dmon/config.yaml` < `./.dmon/config.local.yaml` (project config now correctly overrides global; the local layer overrides both).
- **Restore on startup.** `ProviderRegistry` initialises its active provider + model from `IConfiguration["activeModel"]` (parsed to `ModelRef`) when the provider is configured; otherwise the default (index 0).
- **Save on switch.** When a switch is committed, write `activeModel: {provider}/{model}` to `./.dmon/config.local.yaml` (app-owned → a clean rewrite is safe).
- **Fix switch timing.** `TurnHandler` commits any pending switch at the **start** of a turn (before the provider client is resolved), so a between-turns selection is used on that turn; a switch issued mid-turn still defers to the next turn.
- **git-ignore `config.local.yaml`.**

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `provider-registry` — persistence of the active selection as a `{provider}/{model}` ref via a git-ignored `config.local.yaml` IConfiguration layer (restore at startup, save on commit); corrected config-layer precedence (global < project < local).
- `agent-core` — the turn loop commits a pending switch before resolving the provider client, so a between-turns selection is effective on the next turn.

## Impact

- **New primitive:** `ModelRef` in `Dmon.Abstractions.Providers` (parse/format, split-on-first-slash).
- **Code:** `ProviderRegistry` (restore from `IConfiguration["activeModel"]`, save on commit); `TurnHandler` (commit at turn start); the active-model store reduced to a writer over `config.local.yaml` + a `Load()` that reads `IConfiguration`; `Program.cs` (add the `config.local.yaml` layer, fix precedence); DI wiring; `.gitignore`.
- **Config behaviour change:** reordering the existing `config.yaml` layers means project config now overrides global for *all* keys (previously global won). Small blast radius, called out here.
- **Files on disk:** introduces `./.dmon/config.local.yaml` (git-ignored, app-managed).
- **Verification:** tier-A tests for `ModelRef` (parse/format incl. multi-slash + provider-only), the writer (round-trip), registry restore from config, turn-loop commit-at-start. Manual smoke: pick Gemini → used immediately; restart → restored from `config.local.yaml`.
- **Note:** this supersedes the change's earlier two-property (`activeProvider`/`activeModel`) `state.yaml` design committed mid-implementation.
