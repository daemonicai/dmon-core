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
- [First-run provisioning races the gateway's device-store reload](provisioning-races-device-store-reload.md) — the new-device path 401s on its first connect and never retries; observed once, live. Cross-repo: the fix was decided to be gateway-side, so the note stayed here.
- [A `turn.submit` can produce no event at all](turn-submit-can-produce-no-event-at-all.md) — cancellation before `turnStart` reaches the wire is swallowed silently, so a client cannot tell a wedged turn from a slow one.

### Tests
- [`Dmon.Core.Tests` has a recurring intermittent failure](dmon-core-tests-intermittent-failure.md) — ~1 red run in 8; the failing test was not captured, so catch it with its name first.
- [`WizardEngineTests` intermittent failure](wizard-engine-intermittent-failure.md) — the one *identified* sighting.

### Docs and tooling
- [The protocol guide still says `profile` where the wire says `agent`](protocol-guide-uses-profile-not-agent.md) — client-facing guide; a client written from §3.2 fails at its first `create`. The real fix is an end-to-end sweep of the guide against the wire, which is unowned.
- [Stale `Group 5` placeholder comment on a populated Desktop view](stale-group-placeholder-in-desktop-conversation-view.md) — same defect class `dmon-home-foundations` §9 removed from the protocol DTOs; the locative-vs-temporal test for `Group N` comments is recorded there.
- [`make clean` does not clean the Swift tree](make-clean-misses-swift-trees.md) — `daemon/Daemon.App/.build/`.
- [Documentation drift pass](docs-drift-pass.md) — hard-coded ADR count, ADR-013 status mismatch, an orphaned ADR summary.
- [ADR-034 has no record of the Apple Silicon constraint](adr-034-missing-apple-silicon-constraint.md) — a binding constraint that is currently unwritten.

## Moved out of this register

`dmon-home` left this repository (ADR-038), and the fourteen notes describing
Swift supervision, the Swift toolchain and the macOS host's UI went with the code
they describe (ADR-038 Decision 4). They are in `daemonicai/dmon-home`'s own
`tech-debt/`.

The **first-run provisioning race** straddles the boundary and deliberately did
*not* move: the Product Owner settled ADR-038's first open question in favour of
the gateway-side fix, and the repository that owns the fix owns the note.
