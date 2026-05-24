# Tasks: provider-setup-wizard

## Group 1 — Protocol surface

- [x] Add `SetupRequiredEvent` record to `Dmon.Protocol/Events/SystemEvents.cs`
  - Fields: `IReadOnlyList<AdapterInfo> Adapters`
  - Add `AdapterInfo` record: `Name`, `DefaultModelId`, `DefaultEnvVar`, `EnvVarDetected`
- [x] Add `ProviderConfiguredEvent` record to `Dmon.Protocol/Events/SystemEvents.cs`
  - Fields: `string Adapter`, `string ModelId`, `string Scope`
- [x] Add `ProviderConfigureCommand` record to `Dmon.Protocol/Commands/`
  - Fields: `string Adapter`, `string ModelId`, `string EnvVar`, `string Scope` (`"global"` | `"local"`)
- [x] Register new event/command types in any discriminator/serialisation registrations

## Group 2 — Factory metadata

- [x] Add `string DefaultModelId { get; }` and `string DefaultEnvVar { get; }` to `IProviderFactory` in `Dmon.Abstractions`
- [x] Implement in `AnthropicProviderFactory`: `DefaultModelId = "claude-sonnet-4-6"`, `DefaultEnvVar = "ANTHROPIC_API_KEY"`
- [x] Implement in `OpenAiProviderFactory`: `DefaultModelId = "gpt-4o"`, `DefaultEnvVar = "OPENAI_API_KEY"`
- [x] Implement in `GeminiProviderFactory`: `DefaultModelId = "gemini-2.5-pro"`, `DefaultEnvVar = "GEMINI_API_KEY"`

## Group 3 — Core: setup detection

- [x] Remove the `_all.Count == 0` throw from `ProviderRegistry` constructor; allow zero providers
- [x] Update `BootstrapService` to target `~/.dmon/config.yaml` (global) instead of `.dmon/config.yaml` (local) for the initial first-run file creation
- [x] Add `SetupCheckService` to `Dmon.Core/Bootstrap/`
  - Injected with `IEnumerable<ProviderConfig>` and `IEnumerable<IProviderFactory>` and `IEventEmitter`
  - `RunAsync`: if provider count > 0, return (no-op); otherwise check `Environment.GetEnvironmentVariable(factory.DefaultEnvVar)` per factory and emit `SetupRequiredEvent`
- [x] Wire `SetupCheckService.RunAsync` into `RpcHostedService` startup, after `BootstrapService.RunAsync` and before emitting `agentReady`

## Group 4 — Core: setup handler

- [x] Add `ProviderSetupHandler` to `Dmon.Core/Rpc/`
  - Handles `ProviderConfigureCommand`
  - Resolves target path: `~/.dmon/config.yaml` for `scope: global`, `.dmon/config.yaml` for `scope: local`
  - Creates `~/.dmon/` directory if it does not exist
  - If target file does not exist: writes a new file with the provider stanza
  - If target file exists: appends the provider stanza under `providers:` (see design D4 note on YAML append)
  - Emits `ProviderConfiguredEvent` on success; emits `ErrorEvent` on failure
- [x] Register `ProviderSetupHandler` in `CommandDispatcher` / `DaemonServiceExtensions`

## Group 5 — Console: wizard and lifecycle

- [ ] Add `SetupWizard` class to `Dmon.Console/`
  - `Show(IReadOnlyList<AdapterInfo> adapters, bool isAddProvider)` — synchronous Spectre.Console prompts
  - Branches on `envVarDetected` count per design D3
  - When `isAddProvider = true`: always show full adapter picker + scope step (`global` / `local`)
  - Returns `SetupWizardResult { Adapter, ModelId, EnvVar, Scope }`
- [ ] Update `ConsoleHost.RunAsync` to intercept `SetupRequiredEvent` before `agentReady`:
  - Run `SetupWizard.Show(adapters, isAddProvider: false)`
  - Send `ProviderConfigureCommand` to core
  - Wait for `ProviderConfiguredEvent` (or `ErrorEvent`)
  - On success: call `StopAsync` + `StartAsync` on `CoreProcessManager` (restart core), then resume normal startup
  - On error: display message and exit
- [ ] Update `ConsoleHost.ProcessEventAsync` to handle `ProviderConfiguredEvent` in the mid-session (post-ready) path (for `/add-provider`)
- [ ] Add `AddProviderCommand` client-side marker type (not sent to core) to `Dmon.Console/`
- [ ] Update `SlashCommandParser` to parse `/add-provider` into `AddProviderCommand`
- [ ] Update `ConsoleHost.ProcessUserInputAsync` to handle `AddProviderCommand`:
  - Run `SetupWizard.Show(adapters, isAddProvider: true)`
  - Send `ProviderConfigureCommand` to core
  - Restart core on `ProviderConfiguredEvent`
  - Note: requires the console to cache the `adapters` list received at startup for reuse here

## Group 6 — Tests

- [ ] Unit tests for `SetupCheckService`:
  - No providers configured → `SetupRequiredEvent` emitted with correct `EnvVarDetected` flags
  - One provider configured → no event emitted
  - Env var present in environment → `EnvVarDetected = true` for that adapter
- [ ] Unit tests for `ProviderSetupHandler`:
  - New global file created with correct YAML content
  - New local file created with correct YAML content
  - Existing file appended with new provider stanza
  - `ProviderConfiguredEvent` emitted on success
- [ ] Integration test for first-run flow (extend `IntegrationSmokeTest`):
  - Core started with no config → `setupRequired` event received
  - `provider.configure` command sent → `providerConfigured` event received
  - Config file written at expected path
