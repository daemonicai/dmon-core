## Context

`Dmon.Tools.Dmail` established the pattern for personal-data tools: a thin HTTP client extension in the monorepo calls a companion server that owns local indexing. Calendar follows that pattern exactly. The companion server (`Daemon.Calendar`) is Daemon-specific infrastructure; it lands in `daemon/` even though the ADR-025 amendment that formally adds `daemon/` as a named bucket is Phase 3's responsibility. The thin client (`Dmon.Tools.Calendar`) is a generic reusable package — any agent can consume it given a running calendar server.

The `terminal-client-factory` change (Phase 1) introduced `IAbilityProvider`, `AbilityRegistry`, and the opaque string scope label. Calendar tools must register with scope `"personal"` so `AbilityRegistry` gates them correctly: they appear only in personal-scope turn manifests and never in world-scope ones. `Dmon.Tools.Calendar` declares the literal `"personal"` and takes no dependency on the Daemon.

## Goals / Non-Goals

**Goals:**
- `Dmon.Tools.Calendar` package: `CalendarExtension : IToolExtension`, `CalendarClient` (HTTP), `ICalendarStore`, `CalendarAbilityProvider : IAbilityProvider` (scope `"personal"`), `AddCalendarAbilities()` verb.
- `Daemon.Calendar` server: ASP.NET Core minimal-API, iCal → SQLite (FTS5) sync, search/lookup HTTP API.
- Deterministic matching in the server: the model supplies `term`/`after`; SQLite does the match; the model formats returned fields verbatim.
- Offline-capable: once synced, lookups require no network.
- ADR-005 compliant: iCal subscription URL is a static credential (no OAuth).

**Non-Goals:**
- Write operations (creating, moving, or deleting calendar events).
- Semantic/vector search over event content (FTS5 is sufficient for the milestone).
- Multi-calendar aggregation (single iCal URL per instance for now).
- ADR-025 amendment for the `daemon/` bucket (Phase 3).
- Google Calendar API direct integration (iCal subscription URL is the data source).

## Decisions

### D1: Follow the dmail pattern — separate server + thin HTTP client

`Dmon.Tools.Calendar` is a thin HTTP client, mirroring `Dmon.Tools.Dmail` exactly:
- `CalendarClient` wraps `HttpClient`, authenticates with `X-Api-Key`, serialises with `JsonSerializerDefaults.Web`.
- `CalendarExtension : IToolExtension` builds `AIFunction`s over `CalendarClient` methods (via `DmonAIFunctionFactory.Create()`, as `Dmon.Tools.Dmail` does), exposes them via `Tools`, and implements `PermissionResult Evaluate(...)`. `Evaluate` returns `PermissionResult.Allow` (the enum is `{ Allow, Prompt, Deny }` — there is no `Read` value; "no prompt" maps to `Allow`) for **both** tools: calendar metadata and the full event body are all already-structured fields with no disclosure beyond what the model asked for. This is a deliberate divergence from dmail, which returns `Prompt` for full-message retrieval (`get_email`); calendar event bodies are lower-sensitivity than email bodies, so both calendar tools are `Allow`.
- Configured via `DCAL_BASE_URL` (default `http://localhost:5280`) and `DCAL_API_KEY` env vars.

**Alternative considered:** Embed SQLite directly in `Dmon.Tools.Calendar` (in-process). Rejected: the server process owns the sync scheduler and is the correct place for background work. Keeping sync out of the tool extension also means the calendar stays up-to-date even when no agent session is active.

### D2: Daemon.Calendar lands in daemon/ now

`Daemon.Calendar` is Daemon-specific (it has no use outside the Daemon agent). The `daemon/` directory is created by this change even though the formal ADR-025 amendment is Phase 3. The directory is not yet listed in `Everything.slnx` until Phase 3 adds it properly; for now `Daemon.Calendar` is built via its own `daemon.slnx` (created by this change) and included in `Everything.slnx` immediately. If Phase 3 formalises the bucket differently, moving the project is a low-cost rename.

### D3: SQLite with FTS5 virtual table for event matching

Schema:
```sql
CREATE TABLE events (
    uid         TEXT PRIMARY KEY,
    title       TEXT NOT NULL,
    description TEXT,
    location    TEXT,
    start_utc   TEXT NOT NULL,   -- ISO-8601
    end_utc     TEXT NOT NULL,
    last_sync   TEXT NOT NULL
);
CREATE VIRTUAL TABLE events_fts USING fts5(
    uid UNINDEXED, title, description,
    content=events, content_rowid=rowid
);
```

**External-content FTS5 maintenance:** an `events_fts` table declared with `content=events` is *not* auto-populated by inserts into `events` — the contentless/external-content table must be filled explicitly (or via triggers). With the full-replace strategy (D4), `CalendarDatabase.Clear()` must also clear `events_fts` and `Upsert(...)` must insert the matching `events_fts` rows; otherwise `FindNext`'s `MATCH` returns nothing.

**Canonical timestamp format:** `start_utc`/`end_utc` are stored as TEXT and compared lexically (`start_utc >= :after`). Lexical comparison is only correct if every stored value *and* the `after` default share one canonical UTC format. Both the sync writer (D4) and the `after` default (D7 / `GET /api/events/next`) MUST use the identical format — `DateTimeOffset`/`DateTime` normalised to UTC and formatted as `yyyy-MM-ddTHH:mm:ssZ` (fixed precision, zero offset). Mixed precision or local offsets silently mis-filter.

`FindNext(term, after)` runs:
```sql
SELECT e.* FROM events e
JOIN events_fts f ON e.rowid = f.rowid
WHERE events_fts MATCH :term
  AND e.start_utc >= :after
ORDER BY e.start_utc ASC
LIMIT 1
```

FTS5 MATCH handles partial word matching. For the "eyebrow appointment" case, the term `eyebrow` matches any event whose title or description contains that word. The model is never given a list of events to reason over.

**Alternative considered:** SQLite `LIKE '%term%'` without FTS5. Simpler but slower on large calendars and no stemming. FTS5 is built into SQLite and requires no extra dependency.

### D4: Full-replace sync strategy

On each sync cycle, `Daemon.Calendar` downloads the iCal URL, parses all events with `Ical.Net`, clears the `events` table, and re-inserts. For typical personal calendar sizes (hundreds to low thousands of events), a full replace completes in < 100ms and is trivially correct. Incremental diffing by UID would be more efficient but adds complexity for negligible gain at these scales.

**Dependency:** `Ical.Net` NuGet package in `Daemon.Calendar`. No SDK dependency in `Dmon.Tools.Calendar`.

### D5: Background sync as IHostedService

`CalendarSyncService : BackgroundService` runs inside `Daemon.Calendar`. On startup it performs an immediate sync, then sleeps for `SyncIntervalMinutes` (default 15, configurable via `appsettings.json` / env var `DCAL_SYNC_INTERVAL_MINUTES`) between cycles. A `POST /api/sync` endpoint triggers an immediate out-of-cycle sync (useful for testing and for the menu bar app's "sync now" button in Phase 3).

### D6: Dual registration — IToolExtension + IAbilityProvider

`AddCalendarAbilities()` on `IToolRegistration` registers both:
1. `CalendarExtension` as `IToolExtension` — puts `lookup_calendar` and `list_upcoming_events` into the global tool pipeline for non-triage agents.
2. `CalendarAbilityProvider` as `IAbilityProvider` (scope `"personal"`) — makes the same tools available via `AbilityRegistry.ForScope("personal")` in triage-enabled agents.

This means the same tools work correctly whether the caller uses a simple single-provider composition root or a full `TriageRouter` composition root. The two registrations are independent; the tools are not duplicated in the pipeline (the global `IToolExtension` path and the triage `IAbilityProvider` path are exclusive — triage replaces the global pipeline).

**Load-bearing assumption:** the no-duplication property rests on triage *replacing* the global tool pipeline rather than augmenting it. `TriageRouter` itself lands in Phase 3 (`daemon/`), so this change cannot exercise the full triage path. What it *can* and does verify (group 8): registering both via `AddCalendarAbilities()` yields each tool exactly once in the global `IToolExtension` list **and** in `AbilityRegistry.ForScope("personal")`, with `IAbilityProvider` tools never leaking into the global list (the Phase 1 invariant). The runtime exclusivity under triage is re-verified when Phase 3 wires the router.

### D7: HTTP API surface for Daemon.Calendar

```
GET /api/events/next?term={term}&after={iso8601}
    → CalendarEvent? (null → 404)

GET /api/events/upcoming?maxResults={n}&after={iso8601}
    → CalendarEvent[]

POST /api/sync
    → 204 No Content (triggers immediate sync)

GET /health
    → 200 OK (last_sync timestamp, event count)
```

`CalendarEvent` DTO: `{ uid, title, description, location, startUtc, endUtc }`.

## Risks / Trade-offs

- **iCal URL staleness:** If the device is offline for an extended period, the local store reflects the last-synced snapshot. Events added since last sync will not appear. Mitigation: `GET /health` exposes `last_sync`; the tool description can mention "as of last sync" in its response when the snapshot is old.
- **FTS5 rebuild after full replace:** Clearing and reinserting all rows rebuilds the FTS5 index each cycle. Negligible for < 10,000 events. If a user has an unusually large calendar, consider `INSERT OR REPLACE` with FTS5 triggers instead.
- **DCAL_API_KEY optional:** Following the dmail pattern, the API key is optional (null means no auth). For local-only deployment on a trusted machine this is acceptable; for the Daemon's Mac mini the key should be set.
- **Ical.Net recurrence expansion:** Recurring events (e.g. "weekly standup") must be expanded into individual occurrences for `FindNext` to work correctly. `Ical.Net` supports occurrence expansion; the sync service should expand a configurable horizon (default: 90 days ahead).

## Open Questions

- **OQ-A (recurrence horizon):** 90 days is a guess. A user with events booked far in advance (e.g. a concert next year) won't find them via `lookup_calendar`. Make the horizon configurable (`DCAL_RECURRENCE_HORIZON_DAYS`, default 90); document the limitation.
- **OQ-B (multiple calendars):** A single `DCAL_ICAL_URL` is the V1 scope. If the user has separate work and personal calendars, they need separate server instances. A multi-URL approach is a natural follow-on; leave `ICalendarStore` and the API shape clean for it.
