## ADDED Requirements

### Requirement: Provider extensions are populated by build-time DI-discovery
`IProviderRegistry` SHALL be populated with provider extensions at **build time** by enumerating `IEnumerable<IProviderExtension>` from the DI container and routing each through the existing `IProviderRegistry.RegisterExtensionAsync`, gated by `IsApplicable()`. There SHALL be no manual post-build registration loop and no dynamic-loader path: the registry's provider-extension contents are exactly the applicable `IProviderExtension` instances registered in DI (via `AddProvider<T>()` / `Use<Provider>` verbs). This population SHALL NOT alter the registry's runtime/session-state surface (`GetCurrentAsync`, `SetProvider`, `SetModel`); it only changes how the registry comes to hold its providers.

#### Scenario: Registry enumerates provider extensions from DI at build time
- **WHEN** the host is built and the container holds registered `IProviderExtension` instances
- **THEN** the registry enumerates `IEnumerable<IProviderExtension>` and routes each applicable one via `RegisterExtensionAsync`, with no separate manual or dynamic-loader registration step

#### Scenario: Inapplicable provider extension is not registered
- **WHEN** a registered `IProviderExtension` returns `false` from `IsApplicable()` during build-time enumeration
- **THEN** it is skipped and does not appear in the registry

#### Scenario: Runtime selection surface is unchanged
- **WHEN** providers have been populated by build-time DI-discovery
- **THEN** `GetCurrentAsync`, `SetProvider`, and `SetModel` behave exactly as before; only the population path has changed
