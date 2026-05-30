# sub-agent-extensions Specification

## Purpose
TBD - created by archiving change sub-agent-extensions. Update Purpose after archive.
## Requirements
### Requirement: Sub-agent tool extensions are within scope
The system SHALL treat a tool extension that constructs and invokes a scoped, single-turn in-process `IChatClient` as a supported pattern, distinct from the multi-agent orchestration deferred in V1. Multi-agent orchestration SHALL be defined as multiple `dmon-core` processes communicating over the stdio/RPC interface. This boundary SHALL be recorded in an accepted ADR.

#### Scenario: Scope boundary recorded
- **WHEN** the change is implemented
- **THEN** an accepted ADR in `docs/adrs/` states that an in-process scoped sub-agent `IChatClient` used to fulfil a tool call is not multi-agent orchestration, and defines orchestration as multiple `dmon-core` processes over stdio/RPC

#### Scenario: Single-turn sub-agent is permitted
- **WHEN** an extension fulfils a tool call by running one scoped sub-agent request
- **THEN** this is permitted by the contract and does not constitute the deferred multi-agent orchestration

### Requirement: Independent sub-agent client construction
An extension SHALL be able to construct an independent sub-agent `IChatClient` using only public contract types, by resolving `IProviderFactory` instances from the injected `IServiceProvider`, selecting one by `AdapterName`, resolving the provider credential from `DefaultEnvVar`, and calling `CreateAsync`. The extension SHALL NOT obtain its sub-agent client from `IProviderRegistry` or otherwise mutate the primary agent's provider state.

#### Scenario: Factory resolution from the injected provider
- **WHEN** an extension constructed with an `IServiceProvider` requests the provider factories
- **THEN** `IEnumerable<IProviderFactory>` resolves from that `IServiceProvider`

#### Scenario: Independent client built via CreateAsync
- **WHEN** an extension builds a sub-agent client for a given `<adapter>/<model-id>`
- **THEN** it uses the matching factory's `CreateAsync` to obtain a fresh client and does not call `IProviderRegistry.GetCurrentAsync` or `SetModel`

#### Scenario: Primary agent state is unaffected
- **WHEN** a sub-agent client is created and used
- **THEN** the primary agent's current provider and model are unchanged

### Requirement: Extensions can resolve configuration from the injected provider
The `IServiceProvider` handed to a loaded extension's constructor SHALL resolve `IConfiguration`, so an extension can read its own settings. This guarantee SHALL be covered by a loader test.

#### Scenario: IConfiguration is resolvable
- **WHEN** an extension is instantiated with an `IServiceProvider`
- **THEN** `IConfiguration` can be resolved from that provider

### Requirement: Per-command-extension configuration section
A command (sub-agent) extension SHALL be able to read its own settings from a named configuration section keyed by the extension's name, consistent with the middleware configuration mechanism. The section SHALL carry arbitrary fields, including at minimum a `model` value of the form `<adapter>/<model-id>`.

#### Scenario: Extension reads its model setting
- **WHEN** a command extension named `dmon-websearch` has a configured `model` of `gemini/gemini-2.5-flash` in its named section
- **THEN** the extension reads `gemini/gemini-2.5-flash` from that section via the injected `IConfiguration`

#### Scenario: Name-keyed section survives layered config
- **WHEN** the named command-extension section is present in either user or project config
- **THEN** the value is read correctly without the array-index collapse that affects the layered `extensions` list

