# Tech debt register

One file per item. Each note is self-contained: what it is, why it matters, what
to do, and where the finding came from.

This exists because parked items were previously living only in a change's
`DEVLOG.md` `## NEXT` section — which is accurate and detailed, but scoped to
one change, archived with it, and read only by whoever is working that change.
Debt that outlives a change needs somewhere that outlives a change.

## Conventions

- **Filename** is a kebab-case slug of the problem, not of the fix.
- **Status** is `open`, `resolved` (with the commit), or `superseded` (with a
  pointer). Resolved notes stay — the provenance is the point.
- **Provenance matters.** Say who found it and whether the claim was *verified*
  or *inferred*. A note that overstates its evidence is worse than no note,
  because the next reader budgets against it.
- **Do not restate what the code or the DEVLOG already says.** Link to it.

## Open items

### Behaviour gaps
- [Silent failure when a child's executable cannot be resolved](silent-failure-on-unresolved-launch.md) — the most likely real-world failure shows no signal in any of the three UI panes.
- [A crashed child's descendants are never group-killed](crashed-child-descendants-not-group-killed.md) — survivors leak for the host's lifetime.
- [`.process` health checks are unimplemented](process-health-checks-unimplemented.md) — `HealthChecker` returns `.unknown` without executing anything.
- [A `turn.submit` can produce no event at all](turn-submit-can-produce-no-event-at-all.md) — cancellation before `turnStart` reaches the wire is swallowed silently, so a client cannot tell a wedged turn from a slow one.

### Structural / by-convention-only
- [`withTimeout` cannot bound work that ignores cancellation](timeout-race-cannot-bound-uncooperative-work.md) — mechanism proven by repro; the two `Supervisor` call sites are unaudited.
- [The supervisor's shutdown walk is cancellable by construction](shutdown-walk-is-cancellable.md) — a future edit can skip children with no compiler or test signal.
- [Observer single-construction is convention, not construction](observer-single-construction-by-convention.md) — two instances now; a third makes it worth closing.
- [The termination path is load-bearing on process-scoped resources only](termination-path-process-scoped-only.md) — adding any other kind breaks it silently.
- [Gateway enablement is a build-time constant](gateway-enablement-is-static.md) — "enabled" cannot yet mean "actually serving".
- [Three near-identical stores](store-duplication-trigger.md) — deliberately not collapsed; the trigger to revisit is recorded.

### Toolchain
- [A closed `WebSocketGatewayTransport` can leave its read loop running forever](websocket-receive-cancellation-leak.md) — contained and tested; the residual leak needs a live socket to confirm or fix.
- [Swift 6.3.3 async task-context crash, worked around twice](swift-task-dealloc-workarounds.md) — see also `home/TOOLCHAIN-NOTES.md`.

### Tests
- [`.timeLimit` does not bound a hang on an un-cancellable continuation](swift-testing-timelimit-does-not-bound-continuation-hangs.md) — two hang-shaped regression tests carry a trait that cannot bound them; reproduced twice, and only an external timeout can work.
- [`ChildSpawnerTests` readiness rests on a fixed sleep](childspawner-test-timing-assumption.md).
- [`Dmon.Core.Tests` has a recurring intermittent failure](dmon-core-tests-intermittent-failure.md) — ~1 red run in 8; the failing test was not captured, so catch it with its name first.
- [`WizardEngineTests` intermittent failure](wizard-engine-intermittent-failure.md) — .NET side, unrelated to `home/`; the one *identified* sighting.

### Docs and tooling
- [`make clean` cleans neither Swift tree](make-clean-misses-swift-trees.md).
- [Documentation drift pass](docs-drift-pass.md) — hard-coded ADR count, ADR-013 status mismatch, an orphaned ADR summary.
- [ADR-034 has no record of the Apple Silicon constraint](adr-034-missing-apple-silicon-constraint.md) — a binding constraint that is currently unwritten.
