## Why

The terminal has no way to switch AI provider or model at runtime — users must restart with a different config. The `/model` command fills this gap with an interactive two-step picker: provider first, then the live model list for that provider, enabling seamless mid-session switching.

## What Changes

- New `/model` command parsed by `ConsoleEventHandler`
- Two-step interactive arrow-key picker in the terminal (provider → model)
- New `ModelModelsCommand { Provider }` RPC command to fetch models for a specific provider
- New `ModelModelsResultEvent { Provider, Models, ActiveModelId }` RPC event carrying the live model list
- `ProviderRegistry` gains a committed `_activeModelId` field (currently only the terminal's local state tracks this after a switch)
- `ModelListResultEvent.ActiveModelId` sourced from runtime registry (not `DefaultModelId`)
- `ModelModelsHandler` in `Dmon.Core` to handle the new command
- Terminal shows loading state between provider selection and model list arrival
- Escape cancels either picker without sending any command

## Capabilities

### New Capabilities

- `model-switcher`: Interactive `/model` command — two-step picker to switch provider and model at runtime, with live model fetching and pre-selection of the active provider/model

### Modified Capabilities

- `provider-registry`: `ProviderRegistry` must expose committed active model ID at runtime (currently only stored as pending, then discarded after commit)
- `provider-model-listing`: `ModelListResultEvent.ActiveModelId` must reflect runtime state, not config default; new `ModelModelsCommand`/`ModelModelsResultEvent` pair extends the listing protocol

## Impact

- `Dmon.Protocol`: new command type `ModelModelsCommand`, new event type `ModelModelsResultEvent`
- `Dmon.Core`: new handler `ModelModelsHandler`, updated `ModelListHandler`, updated `ProviderRegistry` and `IProviderRegistry`
- `Dmon.Terminal`: `ConsoleEventHandler` handles `/model` input and two new events; `TerminalRenderer` renders arrow-key pickers with loading state
