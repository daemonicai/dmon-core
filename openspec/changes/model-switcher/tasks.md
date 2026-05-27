## 1. Protocol — new command and event types

- [ ] 1.1 Add `ModelModelsCommand` record to `src/Dmon.Protocol/Commands/ModelCommands.cs` with `[JsonPropertyName("provider")] public required string Provider { get; init; }`
- [ ] 1.2 Add `ModelModelsResultEvent` record to `src/Dmon.Protocol/Events/ModelEvents.cs` with `Provider` (string), `Models` (IReadOnlyList<string>), and `ActiveModelId` (string?) properties
- [ ] 1.3 Register `ModelModelsCommand` in `Command.cs` JsonDerivedType attributes (type discriminator `"model.models"`)
- [ ] 1.4 Register `ModelModelsResultEvent` in `Event.cs` JsonDerivedType attributes (type discriminator `"model.models.result"`)

## 2. Registry — committed active model ID

- [ ] 2.1 Add `string? _activeModelId` field to `ProviderRegistry` and update it inside `CommitPendingSwitch()` when `_pendingModelId` is applied
- [ ] 2.2 Add `string? GetCurrentModelId()` to `IProviderRegistry` interface and implement it on `ProviderRegistry` to return `_activeModelId`

## 3. Core — handlers and wiring

- [ ] 3.1 Fix `ModelListHandler.Handle()` to source `ActiveModelId` from `IProviderRegistry.GetCurrentModelId()`, falling back to `current.DefaultModelId ?? string.Empty` when null
- [ ] 3.2 Add `IModelHandler` method `ModelsAsync(ModelModelsCommand cmd, CancellationToken ct)` to `IModelHandler` interface
- [ ] 3.3 Implement `ModelModelsHandler` in `src/Dmon.Core/Providers/`: resolve credentials for `cmd.Provider` from the registry config, call `factory.GetAvailableModelsAsync(apiKey, ct)` with 5-second timeout, emit `ModelModelsResultEvent { Provider, Models = modelIds, ActiveModelId = registry.GetCurrentModelId() }`
- [ ] 3.4 Fix `NullModelHandler.ListAsync` to call `ModelListHandler.Handle()` and emit a proper `ModelListResultEvent` (not the generic `ResponseEvent` stub)
- [ ] 3.5 Implement `NullModelHandler.ModelsAsync` by delegating to `ModelModelsHandler`
- [ ] 3.6 Register `ModelModelsHandler` in `DaemonServiceExtensions.cs`
- [ ] 3.7 Wire `"model.models"` in `CommandDispatcher.RouteAsync` to call `_model.ModelsAsync(...)`

## 4. Terminal — interactive two-step picker

- [ ] 4.1 In `ConsoleEventHandler`, add `/model` to the command parser: send `ModelListCommand` and lock input (same mechanism as wizard)
- [ ] 4.2 Handle `ModelListResultEvent` in `ConsoleEventHandler`: when received in response to a `/model` command (not a spontaneous event), display an arrow-key provider picker pre-selected on `ActiveProvider`; on confirm send `ModelModelsCommand { Provider }`; on Escape unlock input and cancel
- [ ] 4.3 Show a loading indicator (e.g., "Fetching models…") in the terminal between provider selection and `ModelModelsResultEvent` arrival
- [ ] 4.4 Handle `ModelModelsResultEvent` in `ConsoleEventHandler`: display an arrow-key model picker; pre-select the model matching `ActiveModelId` only if the selected provider equals the currently active provider (otherwise select index 0); on confirm send `ModelSetCommand { Provider, ModelId }`; on Escape unlock input and cancel
- [ ] 4.5 Implement the arrow-key picker as a synchronous console loop (polling `Console.KeyAvailable`) inside `TerminalRenderer` or a new `ConsolePicker` helper — UpArrow/DownArrow move selection, Enter confirms, Escape cancels; render selected item highlighted
