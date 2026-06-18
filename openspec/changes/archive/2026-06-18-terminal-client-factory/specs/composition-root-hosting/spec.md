## ADDED Requirements

### Requirement: ITerminalClientFactory replaces the provider-registry terminal client
`ITerminalClientFactory` SHALL be a public interface in `Dmon.Abstractions` declaring `IChatClient Create(IServiceProvider services)`. When exactly one `ITerminalClientFactory` is registered in DI, the host SHALL — at the point where it materializes the terminal `IChatClient` (the per-turn resolution that otherwise takes the provider-registry active provider) — use the factory's `Create(services)` output as the base terminal client, in place of the provider-registry active provider. The existing wrappers (middleware fold, retry, function-invocation, permission gate) SHALL apply to the factory's output exactly as they apply to a provider-registry client. When no factory is registered, the host SHALL use the provider-registry active provider exactly as before; the no-factory path SHALL be behaviourally unchanged.

Note: `DmonHostBuilder.Build()` wires the DI registries and returns the host; it does not itself construct the terminal client (the host resolves it downstream, per turn). The factory is therefore resolved and invoked at that downstream materialization point, after all DI registration, so `Create` can resolve already-registered backends from the same `IServiceProvider`.

#### Scenario: Registered factory supplies the terminal client
- **WHEN** an `ITerminalClientFactory` is registered and a turn is run
- **THEN** the base terminal `IChatClient` is the factory's `Create(...)` output, not the provider-registry active provider

#### Scenario: No factory leaves the existing path unchanged
- **WHEN** no `ITerminalClientFactory` is registered and a turn is run
- **THEN** the host uses the provider-registry active provider as the base terminal `IChatClient`, as before

#### Scenario: Factory output flows through the existing pipeline
- **WHEN** an `ITerminalClientFactory` is registered
- **THEN** the factory's output is wrapped by the same middleware fold, retry, function-invocation, and permission-gate layers that wrap a provider-registry client
