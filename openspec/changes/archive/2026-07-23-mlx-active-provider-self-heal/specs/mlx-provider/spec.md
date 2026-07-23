## ADDED Requirements

### Requirement: Active-provider terminal client self-heals the runtime

When MLX is registered as the **active** provider (via `UseMlx`), the terminal `IChatClient` produced by the provider factory (`IProviderFactory.CreateAsync`) SHALL start and self-heal the runtime. Before dispatching any request the client SHALL ensure the runtime is running via the attach-first `EnsureRunningAsync()` lifecycle — a no-op beyond the readiness check when a healthy server is already serving the port, a respawn when the runtime was torn down. The factory SHALL invoke `EnsureRunningAsync()` (which runs the one-time tool-calling probe) **before** the client's advertised capabilities are determined, so the client's `SupportsToolCalling` reflects the verified probe result rather than a pre-probe default.

This self-heal SHALL be the single source of truth in the factory. The keyed router-backend client-construction helper (`MlxClient(key)`) SHALL obtain its self-heal from the factory-produced client rather than re-implementing a duplicate `EnsureRunningAsync` call or wrapper, so both the active-provider path and the keyed router-backend path share one self-heal mechanism.

#### Scenario: First turn on a cold active-provider agent starts the runtime
- **WHEN** an agent composed with `UseMlx(modelId, port)` issues its first request and no server is running on the port
- **THEN** the terminal client spawns (or attaches to) the runtime and dispatches the request to a live server, rather than failing with a connection-refused error

#### Scenario: Active-provider client self-heals per request
- **WHEN** the active-provider client issues a request after the runtime was torn down
- **THEN** the client re-runs the attach-first `EnsureRunningAsync()` and respawns the runtime before dispatching, without a fresh composition

#### Scenario: Active-provider client advertises verified tool-calling
- **WHEN** the terminal client for a `UseMlx` registration is constructed and the runtime's tool-calling probe verifies tool support
- **THEN** the client reports `SupportsToolCalling == true`, so the agent offers tools on the active-provider path

#### Scenario: Keyed router-backend client remains self-healing via the single source
- **WHEN** the daemon constructs a runtime client through the keyed helper `MlxClient(key)`
- **THEN** the returned client is self-healing (unchanged behaviour), obtained from the factory's self-healing client rather than a duplicate wrapper
