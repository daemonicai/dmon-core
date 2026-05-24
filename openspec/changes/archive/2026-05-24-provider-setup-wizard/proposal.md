## Why

Running `dmon` for the first time requires manually editing a YAML config file before the agent will work. There is no first-run experience: bootstrap creates `.dmon/config.yaml` with only comments, the core crashes if no providers are configured, and the user has no guidance on what to add. This change introduces a first-run setup wizard that detects missing configuration, discovers installed providers, checks for API keys already present in the environment, and walks the user through picking a provider and model. The same wizard is also available mid-session as `/add-provider`.

## What Changes

- **`IProviderFactory` gains metadata properties** — `DefaultModelId` and `DefaultEnvVar` so the wizard can present sensible defaults without hardcoding them in the console.
- **`ProviderRegistry` tolerates zero providers** — currently throws in its constructor if no providers are configured. Changed to a no-op that enables the `NullModelHandler` path, allowing the core to start and run the setup flow.
- **`BootstrapService` targets global config** — first-run bootstrap writes `~/.dmon/config.yaml` (user-global) rather than `.dmon/config.yaml` (project-local). Provider credentials should be configured once globally, not per-project.
- **New `SetupCheckService`** — runs after bootstrap; if no providers are configured, checks the environment for known API key vars (via `IProviderFactory.DefaultEnvVar`), then emits `setupRequired` with the list of available adapters and which env vars were detected.
- **New `ProviderSetupHandler`** — handles the `provider.configure` RPC command; writes a minimal provider stanza to the target config file (global or local per the `scope` field) and emits `providerConfigured`.
- **New protocol surface** — `setupRequired` event, `providerConfigured` event, `provider.configure` command.
- **New `SetupWizard` in `Dmon.Console`** — Spectre.Console interactive prompts, invoked when `ConsoleHost` receives `setupRequired`. Branches on detected env vars: one found → offer to use it by default; multiple found → offer a choice; none found → full adapter picker.
- **`ConsoleHost` handles setup lifecycle** — intercepts `setupRequired` (before `agentReady`), runs the wizard, sends `provider.configure`, and on receiving `providerConfigured` restarts the core process so DI picks up the new config.
- **`/add-provider` slash command** — triggers the same wizard mid-session, with an additional local/global scope choice. On completion, restarts the core.

## Capabilities

### New Capabilities

- `provider-setup`: First-run wizard and `/add-provider` command for interactive provider configuration.

### Modified Capabilities

- `provider-registry`: `ProviderRegistry` tolerates zero configs; `IProviderFactory` gains `DefaultModelId` and `DefaultEnvVar`.
- `bootstrap`: `BootstrapService` writes global `~/.dmon/config.yaml` on first run.
- `rpc-protocol`: New `setupRequired` event, `providerConfigured` event, `provider.configure` command.

## Impact

- **`src/Dmon.Abstractions/`** — `IProviderFactory` gains two properties (additive, not breaking for implementors outside this repo).
- **`src/Dmon.Providers/`** — `AnthropicProviderFactory`, `OpenAiProviderFactory`, `GeminiProviderFactory` implement the new properties.
- **`src/Dmon.Core/`** — `ProviderRegistry` softened; `BootstrapService` updated; new `SetupCheckService` and `ProviderSetupHandler`; `CommandDispatcher` wired to the new handler.
- **`src/Dmon.Protocol/`** — new event and command types.
- **`src/Dmon.Console/`** — new `SetupWizard`; `ConsoleHost` handles setup lifecycle and core restart; `SlashCommandParser` gains `/add-provider`.
- **`test/Dmon.Core.Tests/`** — tests for `SetupCheckService` env-var detection logic and `ProviderSetupHandler` config-writing.
