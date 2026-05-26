## Context

dmon has three cloud providers in `Dmon.Providers` (Anthropic, OpenAI, Gemini), all implementing `IProviderFactory` directly and registered at startup. `IProviderExtension` exists in `Dmon.Abstractions` precisely for local-inference runners like Ollama, but has no implementation yet.

OllamaSharp is a mature .NET client for the Ollama REST API. Version 5.x implements `IChatClient` directly via `Microsoft.Extensions.AI`, making it a natural fit. Ollama's default local base URL is `http://localhost:11434`; it also offers a hosted cloud service at `https://ollama.com`.

Constraints: ADR-001 (only `IChatClient`), ADR-006 (dmon must not auto-start external processes without user confirmation), no API key for local Ollama.

## Goals / Non-Goals

**Goals:**
- Add Ollama as a first-class provider with zero API-key friction for local use.
- Implement both `IProviderExtension` (lifecycle) and `IProviderFactory` (client construction + wizard) per the `provider-extension` spec.
- Wizard covers: deployment type → base URL (editable) → model selection from live server.
- Capability heuristic follows the pattern table in the `provider-extension` spec.
- Project is independent (`Dmon.Providers.Ollama`) — no Ollama dependency bleeds into other projects.

**Non-Goals:**
- Auto-installing or managing the Ollama binary — EnsureRunningAsync throws `NotSupportedException`.
- Capability metadata from the server — Ollama's `/api/tags` response includes no capability data; heuristics from model ID are used.
- Cloud authentication — `ollama.com` cloud endpoints are accessed without auth for now.
- Vision/multimodal support beyond the heuristic.

## Decisions

### 1. Separate project `Dmon.Providers.Ollama`

OllamaSharp is a heavyweight dependency users without Ollama shouldn't pay for. Keeping it in its own project mirrors the `Dmon.Providers` separation from `Dmon.Core` and means users can audit the dependency surface clearly. The project follows the same pattern: references `Dmon.Abstractions` only; `Dmon.Core` references it at startup.

**Alternative considered:** Add to `Dmon.Providers` alongside the cloud factories. Rejected — a local-inference provider alongside API-key providers creates a mixed dependency surface and muddles the `Dmon.Providers` build for CI environments without Ollama.

### 2. Implement both `IProviderExtension` and `IProviderFactory`

`IProviderExtension` spec names Ollama explicitly as the canonical example. Implementing it gives the daemon lifecycle hooks (detect running, refuse to auto-start, enumerate models) without the factory needing to know. `OllamaProviderFactory` is created by `OllamaProviderExtension.CreateFactory()` and is also registered directly at startup for the built-in wizard path.

**Alternative considered:** `IProviderFactory` only, matching the cloud providers. Rejected — skips the `IsRunningAsync` check that gives good UX when Ollama isn't running, and leaves `IProviderExtension` unexercised.

### 3. Wizard flow: deployment → base URL → model

Ollama is unusual in having both local and cloud deployment modes. A ChooseOneStep up front lets the wizard set the right URL default without requiring the user to know the correct endpoint. The base-URL step is editable in case the user runs Ollama on a non-default port or remote host.

Model list is fetched live from the configured base URL using `OllamaApiClient`. If the server is unreachable, the wizard shows an empty model list with a hint that Ollama must be running — consistent with the existing `(no models found)` pattern in the cloud factories.

### 4. `DefaultEnvVar = "OLLAMA_HOST"`

Ollama itself reads `OLLAMA_HOST` to override its base URL. Reusing the same env var makes dmon consistent with the Ollama ecosystem. The base-URL TextInputStep reads `OLLAMA_HOST` as its default when set, matching the pattern of API-key steps in the cloud factories.

### 5. `EnsureRunningAsync` throws `NotSupportedException`

ADR-006: the daemon must obtain user confirmation before launching external processes. Ollama is typically a user-managed service (menu-bar app, systemd unit). Providing a meaningful `EnsureRunningAsync` would require knowing how Ollama was installed (Homebrew, direct binary, Docker) and would be fragile. The correct user path is: start Ollama yourself, then configure dmon. The daemon catches `NotSupportedException` and surfaces a clear message.

### 6. Capability heuristic

OllamaSharp's model list (`/api/tags`) includes no capability metadata. Capabilities are derived from model ID patterns as defined in `provider-extension` spec. Conservative default: `SupportsToolCalling = false`, `SupportsReasoning = false` for unrecognised patterns.

## Risks / Trade-offs

- **Ollama not running during wizard** → model step gets an empty list. Mitigation: show "Ollama is not reachable at `{baseUrl}` — start Ollama and re-run setup" as the single option label.
- **OllamaSharp API surface changes** → OllamaSharp is actively maintained; pinning to 5.4.25 and reviewing on upgrade is sufficient for V1.
- **ollama.com cloud endpoint stability** → ollama.com's API contract is not publicly documented; the cloud option is best-effort. Non-goal to guarantee it.
- **Model capability heuristic is imprecise** → conservative defaults mean tool calling may be unavailable for capable models until the user switches to a named model. Acceptable for V1.

## Migration Plan

1. Add `Dmon.Providers.Ollama` to solution.
2. Register `OllamaProviderExtension` / `OllamaProviderFactory` in `DaemonServiceExtensions`.
3. No config migration — new provider only.
4. No breaking changes to existing providers or interfaces.

## Open Questions

- Should `ollama.com` cloud require an auth token in future? Defer — treat as out of scope for V1.
