## ADDED Requirements

### Requirement: ITerminalClientFactory replaces the provider-registry terminal client
`ITerminalClientFactory` SHALL be a public interface in `Dmon.Abstractions` declaring `IChatClient Create(IServiceProvider services)`. When exactly one `ITerminalClientFactory` is registered in DI, `DmonHostBuilder.Build()` SHALL — after provider/middleware DI-discovery — resolve it and use its `Create(host.Services)` output as the terminal `IChatClient`, in place of the provider-registry active provider. When none is registered, `Build()` SHALL use the provider-registry active provider exactly as before; the no-factory path SHALL be behaviourally unchanged.

#### Scenario: Registered factory supplies the terminal client
- **WHEN** an `ITerminalClientFactory` is registered and `Build()` is invoked
- **THEN** the resolved terminal `IChatClient` is the factory's `Create(...)` output, not the provider-registry active provider

#### Scenario: No factory leaves the existing path unchanged
- **WHEN** no `ITerminalClientFactory` is registered and `Build()` is invoked
- **THEN** `Build()` uses the provider-registry active provider as the terminal `IChatClient`, as before

#### Scenario: Factory is resolved after provider and middleware discovery
- **WHEN** `Build()` runs with an `ITerminalClientFactory` registered
- **THEN** the factory's `Create` is called after provider and middleware DI-discovery, so it can resolve already-registered backends from the same `IServiceProvider`
