## Context

`GatewayConnectionEndpoint.HandleCreateAsync` (`src/Dmon.Gateway/GatewayConnectionEndpoint.cs`)
orchestrates the create-success path: it calls `_coreLauncher.StartProtocolCompatibleCoreAsync(...)`
to spawn a `dmoncore` process, drives `DriveSessionHandshakeAsync` over the core's raw stdio,
constructs `new SessionHandler(sessionId, coreSession)` (whose pump starts assigning `seq`),
registers it under the concurrent-handler cap, and replies with a `created` frame. Failure
branches emit `createRejected {core_timeout}`, close `4500`, or `createRejected {cap_reached}`.

The dependencies are concrete:
- `CoreLauncher` (`src/Dmon.Runtime/CoreLauncher.cs`) — concrete class, DI-registered as
  `AddSingleton<CoreLauncher>()`.
- `CoreSession` (`src/Dmon.Runtime/CoreSession.cs`) — `record (CoreProcessManager Process, AgentReadyEvent AgentReady)`.
- `CoreProcessManager` (`src/Dmon.Runtime/CoreProcessManager.cs`) — concrete; its
  `StandardOutput`/`StandardInput` are `StreamReader`/`StreamWriter` from a real `Process`.

Because the launcher and process are concrete and process-backed, `HandleCreateAsync` cannot be
driven without spawning a real OS core. Existing tests (`test/Dmon.Gateway.Tests/GatewayCreateFlowTests.cs`)
therefore test only the pieces — `DriveSessionHandshakeAsync` against `FeedableReader`/`CapturingWriter`,
`SessionHandler` construct+register+attach, and pre-spawn profile rejection with `_coreLauncher = null!`.
The orchestration and its ADR-014 ordering are untested. This change introduces the seam.

## Goals / Non-Goals

**Goals:**
- Make the real `HandleCreateAsync` drivable in-process with a scripted fake core.
- Pin three behaviours: happy-path create→`created`; the ADR-014 seq-ordering invariant
  (handshake results excluded from the seq stream); the failure paths (`core_timeout`, `4500`,
  `cap_reached`).
- Keep production behaviour byte-for-byte identical — pure interface extraction + DI swap.

**Non-Goals:**
- No wire-protocol, control-frame, command, or event shape changes.
- No change to handshake semantics, seq assignment, or replay behaviour.
- No change to how the production core is spawned, resolved, or torn down.
- Not making the core dispatch loop (`RpcHostedService`, hardcoded to `Console.In`) injectable —
  out of scope; the fake substitutes at the gateway↔core boundary, not inside the core.

## Decisions

### Decision: Extract `ICoreLauncher` and `ICoreProcess` (the chosen seam depth)

Introduce two interfaces in `Dmon.Runtime`:

- `ICoreLauncher` — the single method the gateway uses:
  `Task<CoreSession> StartProtocolCompatibleCoreAsync(string? corePathOverride = null, string? workingDirectory = null, Action<string>? onStderrLine = null, CancellationToken cancellationToken = default)`.
  `CoreLauncher` implements it; its signature is unchanged.
- `ICoreProcess` — the process surface `HandleCreateAsync`, `SessionHandler`, and
  `TearDownCoreAsync` actually consume: `TextReader StandardOutput`, `TextWriter StandardInput`,
  `bool IsRunning`, `Task StartAsync(...)`, `Task StopAsync(...)`, `Task RestartAsync(...)`,
  `void Dispose()` (i.e. `IDisposable`). `CoreProcessManager` implements it; members are unchanged.

`CoreSession` becomes `record (ICoreProcess Process, AgentReadyEvent AgentReady)`.
`GatewayConnectionEndpoint`'s launcher field and constructor parameter change from `CoreLauncher`
to `ICoreLauncher`; `Program.cs` registers `AddSingleton<ICoreLauncher, CoreLauncher>()`.

The exact `ICoreProcess` member set is derived from current usage and SHALL be confirmed against
`CoreProcessManager` during implementation (return types and parameter lists copied verbatim from
the concrete class so the extraction is mechanical and behaviour-preserving).

**Why over the alternatives:**
- *Restructure `CoreSession` to expose `TextReader`/`TextWriter` + `IAsyncDisposable` (no `ICoreProcess`):*
  smaller interface surface but changes `CoreSession`'s shape and `SessionHandler`'s constructor,
  and loses `RestartAsync`/`IsRunning` which the live handler needs. Rejected: a leakier, more
  invasive reshape for less fidelity.
- *Test-only factory on the concrete types (no `ICoreProcess`):* smallest production change but the
  fake would still wrap real streams behind a concrete `CoreProcessManager`, which cannot supply a
  scripted, blockable `FeedableReader`. Rejected: doesn't actually unblock the in-process fake.

Clean interfaces at the launcher and process boundary are the least-surprising seam, mirror how
the gateway already abstracts `IGatewayConnection`/`ISessionHandler`, and let the fake reuse the
existing `FeedableReader`/`CapturingWriter` doubles directly.

### Decision: The fake core is a test double over the existing stream doubles

`test/Dmon.Gateway.Tests` gains a `FakeCoreLauncher : ICoreLauncher` returning a `CoreSession`
whose `ICoreProcess` is a `FakeCoreProcess` backed by a `FeedableReader` (stdout the test feeds
scripted `createResult`/`loadResult` + later live events into) and a `CapturingWriter` (stdin the
test asserts the `session.create`/`session.load` commands were written to). `StartAsync`/`StopAsync`/
`RestartAsync`/`Dispose`/`IsRunning` are recorded so teardown assertions (no orphaned core) are
possible. The handshake result lines are scripted so `DriveSessionHandshakeAsync` resolves a known
`sessionId`. This is the same doubles the current piece-wise tests use, now composed behind the
launcher seam.

### Decision: Seq-ordering is asserted via the handler's sequenced output, not internals

The seq-ordering test feeds the handshake results, then a distinct post-handshake event, then
attaches a recording client and asserts the first sequenced event is the post-handshake one —
i.e. `createResult`/`loadResult` never carry a `seq` and are never replayed. This tests the
observable contract (the new spec requirement) rather than reaching into the pump's internal
state.

## Risks / Trade-offs

- **Interface extraction touches `Dmon.Runtime`'s public surface** → Mitigation: additive only
  (new interfaces implemented by existing classes; `CoreSession.Process`'s static type widens from
  a concrete class to an interface it already satisfies). No member signatures change. Whole-solution
  build + full test run is the gate.
- **`ICoreProcess` could drift from `CoreProcessManager`** → Mitigation: derive the member set
  mechanically from current usage and copy signatures verbatim; the compiler enforces conformance.
- **A fake that diverges from real core framing could give false confidence** → Mitigation: the
  fake feeds real wire-serialized lines (`WireSerializerOptions.Default`) through the real
  `DriveSessionHandshakeAsync` and real `SessionHandler`; only process spawn/stdio is faked.
- **Over-cutting the seam (faking inside the core)** → Explicitly a Non-Goal; the seam stops at the
  gateway↔core process boundary.
