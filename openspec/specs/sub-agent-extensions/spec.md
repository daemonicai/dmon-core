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

A sub-agent tool SHALL configure its model by accepting an `Action<IProviderRegistration>` and running it against a **fresh, isolated** `IProviderRegistration` that materializes to a captured `IChatClientFactory` — a minimal contract `ValueTask<IChatClient> CreateAsync(CancellationToken cancellationToken)` declared in `Dmon.Abstractions`. The tool SHALL obtain its sub-agent client only by invoking that captured factory's `CreateAsync`. The factory MUST NOT touch `IProviderRegistry` (neither `GetCurrentAsync`/`SetModel` nor `RegisterExtensionAsync`), so ADR-010 Decision 3's independence rule is **structurally enforced** rather than merely documented, and the primary agent's provider state cannot be read or mutated through this path.

This supplies the mechanism ADR-010 deferred. The prior guidance — that an extension resolve `IEnumerable<IProviderFactory>` from the injected `IServiceProvider`, select by `AdapterName`, and call `CreateAsync` itself, with "no new contract type" — is **superseded** by the `IChatClientFactory` path (ADR-022 Decision 6 reverses ADR-010's "no new contract type" consequence while retaining its scope boundary).

#### Scenario: Sub-agent provider configured via an isolated registration
- **WHEN** a sub-agent tool is composed with `Action<IProviderRegistration>` that calls a provider verb (e.g. `p => p.UseGemini("gemini-3.5-flash-lite")`)
- **THEN** the action runs against a fresh, isolated `IProviderRegistration` that materializes to an `IChatClientFactory` the tool captures

#### Scenario: Factory is independent of the primary registry
- **WHEN** the captured `IChatClientFactory.CreateAsync` is invoked to build a sub-agent client
- **THEN** it constructs the client without reading or mutating `IProviderRegistry`, and the primary agent's current provider and model are unchanged

#### Scenario: IProviderFactory self-resolution path is superseded
- **WHEN** a sub-agent tool needs a non-active model client
- **THEN** the supported mechanism is the captured `IChatClientFactory` from an isolated `IProviderRegistration`, not manual resolution of `IEnumerable<IProviderFactory>` from the injected `IServiceProvider`

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

### Requirement: Sub-agent factory eager structural validation, lazy resolution

The sub-agent `IProviderRegistration` SHALL be validated for **structural** validity at `Build()` time: exactly one provider verb invoked and a model selected. A structurally malformed registration (for example an empty `Action<IProviderRegistration>`) SHALL fail the build as an author error, so a structurally broken composition root does not survive to runtime. Credential resolution (ADR-005 `DefaultEnvVar`) and `IChatClient` construction SHALL be deferred to the first `CreateAsync`; when the required key is absent, `CreateAsync` SHALL throw `InvalidOperationException` naming the missing environment variable. A sub-agent tool that is never invoked SHALL NOT block core startup on credentials. The factory MAY memoize the constructed `IChatClient` across calls.

#### Scenario: Malformed registration fails the build
- **WHEN** a sub-agent tool is composed with an `Action<IProviderRegistration>` that selects no provider or no model
- **THEN** `Build()` fails with an author-facing error and no host is produced

#### Scenario: Credentials resolved lazily at first use
- **WHEN** a sub-agent tool with a structurally valid registration is composed but never invoked
- **THEN** core startup completes without resolving that sub-agent's provider credential

#### Scenario: Missing key surfaces at first CreateAsync
- **WHEN** the captured `IChatClientFactory.CreateAsync` is first invoked and the provider's credential environment variable is not set
- **THEN** it throws `InvalidOperationException` naming the missing environment variable

#### Scenario: Client may be memoized across calls
- **WHEN** `CreateAsync` is invoked more than once on the same captured factory
- **THEN** the factory MAY return a memoized `IChatClient`, and single-turn usage remains a property of how the tool invokes the client, not of client lifetime

### Requirement: Sub-agent usage stays single-turn and single-core

A sub-agent tool SHALL use its captured client for single-turn, scoped requests that fulfil one tool call (ADR-010 Decision 4): a fresh message list per call, no multi-turn inner loops, no tool-nesting, and no sub-agent tool injection. This scope boundary is retained in full from ADR-010 — an in-process scoped sub-agent `IChatClient` is not the deferred multi-agent orchestration (multiple `dmon-core` processes over stdio/RPC). The runtime SHALL keep nothing between calls regardless of whether the factory memoizes the client.

#### Scenario: Single-turn request per tool call
- **WHEN** a sub-agent tool fulfils a tool call
- **THEN** it issues one scoped request with a fresh message list and does not run a multi-turn inner loop or inject further tools

#### Scenario: Not classified as orchestration
- **WHEN** a tool runs one in-process scoped sub-agent request via its captured `IChatClientFactory`
- **THEN** this is permitted and does not constitute the deferred multi-agent orchestration of multiple `dmon-core` processes over stdio/RPC

