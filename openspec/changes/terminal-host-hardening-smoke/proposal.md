## Why

`terminal-host-hardening` shipped four reliability/readability fixes to the terminal host (goodbye separator before teardown, single-shot `/reload`, `volatile IsLocked`, `DrainAsync` exception handling) verified by automated gates and per-group reviewer audits. Its final task — **5.1, a live end-to-end manual smoke** — was deferred: it is blocked on an unrelated `Dmon.Core` MCP/`Microsoft.Extensions.AI` startup crash and on a provider API key being configured (ADR-005), neither of which the hardening change can resolve.

Holding the hardening change open indefinitely behind a blocked manual step is worse than archiving it (its automated gates are green and it is functionally complete). So the deferred smoke is carved into this standalone change: the hardening archives clean, and the verification debt stays tracked and discoverable instead of lingering as an unticked box in an archived change.

## What Changes

- When `Dmon.Core` is runnable and a provider key is configured, run the manual smoke recipe (`make build && build/dmon`; type; `/reload`; Ctrl+C) and record the result in this change.
- Adds a `terminal-host` acceptance requirement stating the two **user-visible** hardening behaviors are confirmed against a *live* core — the end-to-end counterpart to the tier-A/tier-B proxy tests, which (per the hardening change's own notes) could only approximate the real `Program.cs` flow because the host exposes no injection seam.
- **No production code change is expected.** If the smoke surfaces a defect, that defect is fixed under a *new* change — this change only verifies.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `terminal-host` — adds one requirement, "Hardening behaviors verified against a live core", with two live-acceptance scenarios (goodbye separator on Ctrl+C; rapid `/reload` single-shot).
