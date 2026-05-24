## Context

`BootstrapService` creates a stub config file on first run but leaves it empty of providers. `ProviderRegistry` throws if no providers are configured, so the core crashes before it can emit anything useful. The console has no way to surface this failure or guide the user through recovery. The setup wizard bridges this gap: the core detects the missing config, advertises what it can do (available adapters, detected env vars), and the console drives the interactive prompts.

Provider credentials are user-global concerns — an Anthropic API key applies to every project, not just the one in CWD. First-run bootstrap therefore targets `~/.dmon/config.yaml`. Project-local overrides remain available (existing behaviour), but credentials live globally by default.

## Goals / Non-Goals

**Goals:**
- `dmon` works from a clean install with no prior config
- Env vars already set in the shell are detected and offered as defaults
- The same wizard is reachable mid-session via `/add-provider`
- Provider/model selection uses defaults from the factories, not hardcoded console knowledge
- Config is written by the core (it owns the file format); the console only sends a command

**Non-Goals:**
- OAuth / browser-based authentication (ADR-005: API keys only for V1)
- Storing API key values in config (env vars only; the wizard configures *where to look*, not the key itself)
- Editing or removing existing providers (follow-on change)
- Config file validation or linting beyond what `ProviderConfigLoader` already does

## Decisions

### D1: `IProviderFactory` gains `DefaultModelId` and `DefaultEnvVar`

```csharp
public interface IProviderFactory
{
    string AdapterName { get; }
    string DefaultModelId { get; }   // ← new
    string DefaultEnvVar { get; }    // ← new
    ChatClientCapabilities GetCapabilities(string modelId);
    ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default);
}
```

`DefaultModelId` is the recommended starting model for the adapter (e.g. `claude-sonnet-4-6` for Anthropic). `DefaultEnvVar` is the conventional environment variable name for the API key (e.g. `ANTHROPIC_API_KEY`). The setup wizard reads these from the registered factories — there is no lookup table in the console. The core is the single source of truth for what adapters are available and what their defaults are.

**Alternative considered:** Embed defaults in the console as a static dictionary. Rejected: would couple the console to provider knowledge and break whenever a new adapter is added.

### D2: `SetupCheckService` emits `setupRequired` with env-var detection

After bootstrap, `SetupCheckService` is called from `RpcHostedService` before `agentReady` is emitted. It enumerates all registered `IProviderFactory` instances, checks `Environment.GetEnvironmentVariable(factory.DefaultEnvVar)` for each, and emits:

```json
{
  "type": "setupRequired",
  "adapters": [
    {
      "name": "anthropic",
      "defaultModelId": "claude-sonnet-4-6",
      "defaultEnvVar": "ANTHROPIC_API_KEY",
      "envVarDetected": true
    },
    {
      "name": "openai",
      "defaultModelId": "gpt-4o",
      "defaultEnvVar": "OPENAI_API_KEY",
      "envVarDetected": false
    },
    {
      "name": "gemini",
      "defaultModelId": "gemini-2.5-pro",
      "defaultEnvVar": "GEMINI_API_KEY",
      "envVarDetected": false
    }
  ]
}
```

If at least one provider *is* configured, `SetupCheckService` is a no-op and `agentReady` follows normally.

### D3: Console wizard branches on `envVarDetected`

The console `SetupWizard` receives the `setupRequired` payload and branches:

| Detected env vars | Behaviour |
|-------------------|-----------|
| None | Full adapter picker; user selects adapter, confirms or overrides model, types env var name |
| Exactly one | "Found `ANTHROPIC_API_KEY` — use Anthropic (`claude-sonnet-4-6`)? [Y/n]". Y skips to confirm; N shows full picker |
| Multiple | "Found credentials for: Anthropic, Gemini. Which would you like to use?" picker limited to those adapters |

In all paths the final step is a model confirmation ("Use `claude-sonnet-4-6`? Or enter a different model ID:") so the user can override the default without being forced to.

**Rationale:** Users who have already set an API key should not have to re-specify the obvious choice. The single-detected case covers the most common scenario (one provider, one key). Multiple-detected respects the user's existing environment without guessing.

### D4: `provider.configure` command; core writes the YAML

```csharp
public sealed record ProviderConfigureCommand : Command
{
    [JsonPropertyName("adapter")]   public required string Adapter { get; init; }
    [JsonPropertyName("modelId")]   public required string ModelId { get; init; }
    [JsonPropertyName("envVar")]    public required string EnvVar { get; init; }
    [JsonPropertyName("scope")]     public required string Scope { get; init; } // "global" | "local"
}
```

`ProviderSetupHandler` in `Dmon.Core` handles this command. It hand-writes a minimal YAML stanza — no YAML library is added (the structure is trivial and static). Output written to `~/.dmon/config.yaml` for `scope: global` or `.dmon/config.yaml` for `scope: local`. If the file already exists it is appended to (provider stanza added under `providers:`); if not, it is created.

On success the handler emits `providerConfigured { adapter, modelId, scope }`.

**Alternative considered:** Console writes the YAML directly. Rejected: the console would need to know the config file path conventions and YAML format — knowledge that belongs to the core.

### D5: Console restarts the core after `providerConfigured`

After receiving `providerConfigured`, `ConsoleHost` calls `StopAsync` then `StartAsync` on `CoreProcessManager`. The restarted core reads the newly written config file, constructs `ProviderRegistry` with the new provider, and emits the normal `agentReady`. No new reload mechanism is needed.

**Alternative considered:** `IConfigurationRoot.Reload()` + DI re-initialization at runtime. Rejected: `ProviderRegistry` is a singleton constructed by DI; reloading config would not reconstruct it. A process restart is the clean, safe path and maps naturally onto the existing `CoreProcessManager` abstraction.

### D6: `/add-provider` reuses the same wizard and command

`SlashCommandParser` parses `/add-provider` into a new `AddProviderCommand` client-side marker (not sent to the core). `ConsoleHost` intercepts it, runs `SetupWizard` with an additional scope step ("Save to: global (~/.dmon/) / local (.dmon/)"), sends `provider.configure`, and restarts the core on `providerConfigured`.

The wizard always shows the full adapter picker for `/add-provider` — no env-var shortcut, since the user is explicitly choosing to add a *new* provider, potentially different from what they already have.

### D7: `ProviderRegistry` softened to tolerate zero providers

The constructor guard is removed. When `_all.Count == 0`, `GetCurrentConfig()` throws (as before), but construction succeeds. `NullModelHandler` (already present) handles turns when no provider is active. `SetupCheckService` detects the zero-provider state and emits `setupRequired` before a user could submit a turn.

**Risk:** A user could reach the turn-submit path with no providers if setup is cancelled or skipped. `NullModelHandler` returns an appropriate error event in that case, which is already the documented behaviour.

## Risks / Trade-offs

- **Hand-written YAML append** — appending to an existing config file requires parsing just enough structure to find the `providers:` key. A naive string-append could corrupt indentation. Mitigated by: always appending a well-formed stanza and targeting fresh files on first run (the existing file has only comments). If the file has been edited manually and is non-trivial, the append path may produce malformed YAML. Documented behaviour; a proper YAML round-trip can be added in a follow-on.
- **Core restart on configure** — the restart adds ~1–2 seconds to setup. Acceptable given this is a one-time flow.
- **`IProviderFactory` interface addition** — `DefaultModelId` and `DefaultEnvVar` are additive properties. Any third-party factory implementations outside this repo would need to be updated. Acceptable for V1: the interface is not yet published.

## Open Questions

*(none — resolved in explore session)*
