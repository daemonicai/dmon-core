## ADDED Requirements

### Requirement: UseOmlx composition verb
The `Dmon.Providers.Omlx` package SHALL expose a `UseOmlx` fluent composition verb in the `Dmon.Hosting` namespace as an extension method on `IProviderRegistration` (matching the verb grammar of `UseOllama`/`UseMtplx`/`UseGemini`). `UseOmlx` SHALL register an `OmlxProviderExtension` so that oMLX is a lifecycle-managed provider: its `IsApplicable()`, `ListModelsAsync()`, and `EnsureRunningAsync()` participate in the host the same way as other registered providers. `UseOmlx` SHALL NOT force oMLX as the active model when no model is specified (mirroring the non-hijacking `UseMtplx` behavior), so a composition root whose terminal client is supplied by an `ITerminalClientFactory` (e.g. the Daemon's `TriageRouter`) is not overridden by registering oMLX. Configuration SHALL be sourced via the existing `OmlxConfig` resolution order (constructor values, `OMLX_BASE_URL`, `OMLX_API_KEY`, then defaults).

#### Scenario: UseOmlx registers the provider for lifecycle management
- **WHEN** `UseOmlx()` is called on a provider registration
- **THEN** an `OmlxProviderExtension` is registered and resolvable from DI, exposing `IsApplicable`, `ListModelsAsync`, and `EnsureRunningAsync`

#### Scenario: UseOmlx does not hijack the active model
- **WHEN** `UseOmlx()` is called without a model identifier and an `ITerminalClientFactory` supplies the terminal client
- **THEN** oMLX is registered as available but is not forced as the active model

#### Scenario: UseOmlx honors environment configuration
- **WHEN** `OMLX_BASE_URL` is set and `UseOmlx()` is called with no explicit config
- **THEN** the registered provider resolves its base URL from `OMLX_BASE_URL`

---

### Requirement: Per-model client construction for non-active use
`OmlxProviderFactory` SHALL be usable to construct an `IChatClient` for a specific model id without oMLX being the active provider, so a composition root can mint multiple distinct oMLX-served clients (e.g. a first-line and an escalation model) for a router. A composition root SHALL be able to combine `EnsureRunningAsync()` bring-up with per-model client construction so that oMLX is launched on demand when the model is first needed.

#### Scenario: Two distinct models from one oMLX provider
- **WHEN** a composition root requests an oMLX client for model A and another for model B
- **THEN** it receives two independent `IChatClient` instances targeting the same oMLX base URL with their respective model ids

#### Scenario: Bring-up paired with construction
- **WHEN** a composition root constructs an oMLX client for a model while the oMLX server is not running
- **THEN** `EnsureRunningAsync()` can be invoked to launch oMLX before the client issues its first request
