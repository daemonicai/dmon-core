## MODIFIED Requirements

### Requirement: Config-driven provider registry
The system SHALL maintain a registry of named LLM providers, each resolved to an `IChatClient` instance. The provider list SHALL be composed from two sources: (1) **default `ProviderConfig` instances synthesized from the `IProviderFactory` set wired in the composition root** (see the `provider-factories` capability), and (2) **optional** entries in `~/.dmon/config.yaml` (user-global) and `./.dmon/config.yaml` (project). A `config.yaml` `providers:` entry overrides or augments a synthesized default keyed by adapter/name; the `providers:` map is OPTIONAL and a wired provider SHALL be usable with no `config.yaml` entry. Configuration is layered via `IConfiguration` with precedence, lowest to highest: `~/.dmon/config.yaml` < `./.dmon/config.yaml` < `./.dmon/config.local.yaml` (the git-ignored, app-managed local layer). A later layer overrides the same key in an earlier layer. The composed provider list SHALL be visible to every consumer of the shared `IEnumerable<ProviderConfig>` (notably both `ProviderRegistry` and `CredentialResolver`), so a synthesized provider's credentials resolve identically to a config-declared one.

#### Scenario: Provider resolved at startup
- **WHEN** the agent core starts
- **THEN** all providers — synthesized from wired factories and any declared in config — are present in the registry and their `IChatClient` factories are registered

#### Scenario: Unknown adapter at startup
- **WHEN** a `config.yaml` entry specifies an adapter type with no registered `IProviderFactory`
- **THEN** `ProviderRegistry` logs a warning naming the provider and adapter and skips that entry, and startup proceeds (it does NOT throw)

#### Scenario: Project config overrides global
- **WHEN** the same configuration key is set in both `~/.dmon/config.yaml` and `./.dmon/config.yaml`
- **THEN** the project value takes precedence over the global value

#### Scenario: Local layer overrides project and global
- **WHEN** a key (e.g. `activeModel`) is set in `./.dmon/config.local.yaml`
- **THEN** it overrides the same key in `./.dmon/config.yaml` and `~/.dmon/config.yaml`

## ADDED Requirements

### Requirement: Provider list synthesized from wired factories with config override
The system SHALL synthesize a default `ProviderConfig` for each wired cloud `IProviderFactory` whose `AdapterName` is not already the `Adapter` of any `config.yaml`-derived entry (case-insensitive), and SHALL merge these defaults with the config-derived entries into the single shared provider list. Config-derived entries SHALL appear first (preserving existing index-0/default-selection behavior); synthesized defaults SHALL follow in factory registration order (i.e. `Use<Provider>()` order in `Dmon.cs`). When the `providers:` map is absent or empty, the default provider (index 0, absent a restored selection) SHALL be the first wired provider.

#### Scenario: Verb-only provider is usable without a config entry
- **WHEN** `Dmon.cs` wires `UseAnthropic()` and `.dmon/config.yaml` has no `providers:` entry for it
- **THEN** the registry lists an `anthropic` provider whose `DefaultModelId` and credential env var come from `AnthropicProviderFactory`, and a turn can run against it once its API key is present

#### Scenario: Config entry overrides the synthesized default
- **WHEN** a factory for adapter `openai` is wired AND `config.yaml` declares a provider with `adapter: openai` and a custom `baseUrl`/`defaultModelId`
- **THEN** the config-declared entry is used for that adapter and no duplicate default is synthesized for it

#### Scenario: No providers map and no wired factories is fatal
- **WHEN** `config.yaml` has no `providers:` map AND no `Use<Provider>()` verb has been called
- **THEN** `ProviderRegistry` throws `InvalidOperationException` ("At least one provider must be configured.")

#### Scenario: No providers map but a factory is wired is not fatal
- **WHEN** `config.yaml` has no `providers:` map AND at least one `Use<Provider>()` verb has been called
- **THEN** the registry is constructed successfully with the synthesized provider(s) and does not throw
