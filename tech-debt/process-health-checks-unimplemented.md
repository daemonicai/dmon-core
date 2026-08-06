# `.process` health checks are unimplemented

**Status:** open
**Where:** `home/Sources/Supervisor/HealthChecker.swift`; the only declaring descriptor is `ChildInventory.tailscale`
**Surfaced:** 2026-08-04, section 4 of `dmon-home-foundations`
**Severity:** low — no consumer today, but the rule it must follow is easy to get wrong

## What

`HealthCheck.process(executablePath:arguments:)` is representable and declared
(Tailscale's real health source is `tailscale status --json`, a CLI invocation,
not an endpoint), but `HealthChecker` returns `.unknown` for it without
executing anything.

It has no consumer today: monitors are deliberately not fed to the health-check
loop, and `HostRuntime.statusUpdates()` excludes monitors from its merged feed.

## The rule it must follow when implemented

**Run-to-completion-and-inspect-exit. Never a persistent spawn/adopt target.**

This is not a style preference. The child model makes monitors un-spawnable *by
construction* — `MonitorDescriptor` carries no `launch` and no `adoptionPolicy`
at all, rather than a flag every call site must remember to check. A `.process`
check implemented as a long-lived spawn would give a monitor a launch path
through the back door, and the "illegal states are unrepresentable" guarantee
that the descriptor model is built on would have a hole in it.

## What to do

Implement it when something actually consumes a monitor's health — a status feed
for monitors, which does not exist yet. Until then this is honestly-declared
data with no executor, which is better than a fabricated result.

Note the related standing decision: monitors are not polled at all right now
(Product Owner, 2026-08-04), because checking them bought nothing but standing
cost — a DNS+TLS request to an external endpoint every interval, two more
against services this change never starts, and this `.unknown`-only case. The
`monitors` parameter remains so the inventory stays representable and pluggable.
