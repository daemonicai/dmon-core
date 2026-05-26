## Why

dmon currently ships three cloud-only providers (Anthropic, OpenAI, Gemini). Users who want to run models locally — for privacy, cost, or offline use — have no built-in path. Ollama is the dominant local model runner on macOS and Linux; adding first-class support via `OllamaSharp` gives dmon a zero-cost, offline-capable inference option without any API key requirement.

## What Changes

- **New project** `src/Dmon.Providers.Ollama/` added to the solution (`Daemon.slnx`).
- New `OllamaProviderExtension` class implementing `IProviderExtension` — handles lifecycle (detect running, refuse to auto-start) and model enumeration via OllamaSharp.
- New `OllamaProviderFactory` class implementing `IProviderFactory` — wizard flow + `IChatClient` construction via `OllamaApiClient` wrapped in `CapabilitiesDecorator`.
- `OllamaProviderExtension` registered with `IProviderRegistry` at startup in `DaemonServiceExtensions`.
- Wizard flow: deployment choice → base URL (editable) → model selection (live from server).
- No API key required. `DefaultEnvVar` maps to `OLLAMA_HOST` for base URL override.

## Capabilities

### New Capabilities

- `ollama-provider`: Ollama local/cloud inference provider — `IProviderExtension` + `IProviderFactory` implementation using OllamaSharp, with a deployment-aware setup wizard.

### Modified Capabilities

<!-- No existing requirement changes -->

## Impact

- **New dependency**: `OllamaSharp` 5.4.25 (scoped to `Dmon.Providers.Ollama` only).
- **Solution**: `Daemon.slnx` gains a new project entry.
- **Startup**: `DaemonServiceExtensions.cs` registers the new extension.
- **No breaking changes** to existing providers or interfaces.
