## Why

The gateway's session-create **success** path (`GatewayConnectionEndpoint.HandleCreateAsync`)
has no end-to-end test. It can only be exercised by spawning a real `dmoncore` OS process,
because the gateway depends on the concrete `CoreLauncher`/`CoreProcessManager` (whose stdio
comes straight off a `System.Diagnostics.Process`). Today's tests cover the pieces in isolation
but not the orchestration — including the ADR-014-critical ordering that the
`session.create` + `session.load` handshake results are consumed **before** the
`SessionHandler` seq pump starts, so they never enter the per-session event stream. That
ordering is currently protected only by a code comment.

## What Changes

- Introduce `ICoreLauncher` (extracted from `CoreLauncher`) and `ICoreProcess` (extracted from
  `CoreProcessManager`) in `Dmon.Runtime`. `CoreSession` carries `ICoreProcess` instead of the
  concrete `CoreProcessManager`. `Dmon.Gateway` depends on `ICoreLauncher`, registered in DI.
  This is a **pure testability refactor**: production wiring resolves the same concrete types;
  behaviour is byte-for-byte unchanged.
- Make explicit, as a `remote-session-gateway` requirement, the ADR-014 invariant that a
  session's `create`/`load` handshake result events are consumed during create and are therefore
  excluded from the per-session `seq` stream (never sequenced, never replayed on attach).
- Add gateway tests that drive the real `HandleCreateAsync` through a scripted in-process fake
  core (a fake `ICoreLauncher` returning a `CoreSession` backed by the existing
  `FeedableReader`/`CapturingWriter` doubles), covering: (1) happy-path create→`created`,
  (2) the seq-ordering invariant, and (3) the failure paths (`core_timeout`, `4500` close,
  `cap_reached`).
- No wire-protocol, command, or event shape changes. No change to handshake semantics.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `remote-session-gateway`: add a requirement that the create-handshake result events
  (`session.createResult`, `session.loadResult`) are consumed during `create` before the
  handler's sequencing pump starts, and are therefore never assigned a `seq` and never replayed
  on attach. This promotes the ADR-014-implied ordering to a stated, testable behaviour; it does
  not change runtime behaviour.

## Impact

- **Code (`Dmon.Runtime`):** new `ICoreLauncher`, `ICoreProcess`; `CoreLauncher` implements
  `ICoreLauncher`; `CoreProcessManager` implements `ICoreProcess`; `CoreSession.Process` typed as
  `ICoreProcess`.
- **Code (`Dmon.Gateway`):** `GatewayConnectionEndpoint`'s launcher field/DI typed as
  `ICoreLauncher`; `Program.cs` registers `ICoreLauncher` → `CoreLauncher`.
- **Tests (`test/Dmon.Gateway.Tests`):** a fake `ICoreLauncher`/`ICoreProcess` over existing
  stream doubles; new tests for the three scenarios above.
- **Closes:** the gap tracked in memory `project_gateway_create_e2e_test_seam`.
- **No impact:** wire protocol, gateway control frames, handshake sequencing semantics, session
  on-disk layout, the core process itself.
