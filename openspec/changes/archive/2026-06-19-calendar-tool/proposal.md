## Why

The Daemon agent's personal-scope triage cell needs a `lookup_calendar` ability to handle turns like "when's my eyebrow appointment?" — a deterministic structured lookup, not a reasoning task. The ability must be offline-capable (sub-ms, no network round-trip) and follow the same pattern as `Dmon.Tools.Dmail`: a thin HTTP client extension over a companion local server that owns indexing and sync. The companion server (`Daemon.Calendar`) syncs from an iCal subscription URL into a local SQLite store, satisfying the offline-capable requirement without OAuth (iCal subscription URLs are static credentials, compatible with ADR-005).

## What Changes

- New package `tools/Dmon.Tools.Calendar` — a `CalendarExtension : IToolExtension` that exposes `lookup_calendar` and `list_upcoming_events` to the agent via HTTP calls to the Daemon.Calendar server.
- New server project `daemon/Daemon.Calendar` — an ASP.NET Core minimal-API server that syncs an iCal subscription URL into SQLite and serves a search/list HTTP API. Runs as a companion process (same shape as the dmail server).
- `AddCalendarAbilities()` builder verb on `IToolRegistration` for wiring into a standard tool pipeline, and an `IAbilityProvider` implementation (`CalendarAbilityProvider`) that declares scope `"personal"` (the Phase 1 opaque string scope label) for use with `AbilityRegistry` in triage-enabled agents.
- `ICalendarStore` interface in `Dmon.Tools.Calendar` — the contract the HTTP client implements; testable in isolation.

## Capabilities

### New Capabilities

- **calendar-lookup**: Deterministic next-event lookup by free-text term and optional earliest-start datetime. The tool populates parameters; the store does the matching (SQLite FTS5 `LIKE`/FTS over title and description). The model formats returned fields verbatim; it never reasons over a dump of events.
- **calendar-sync**: iCal subscription URL → SQLite sync. Periodic background fetch (configurable interval, default 15 minutes). Incremental update on re-fetch. Runs inside `Daemon.Calendar` server; `Dmon.Tools.Calendar` is blind to sync details.

### Modified Capabilities

_(none)_

### Removed Capabilities

_(none)_
