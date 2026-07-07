# dcal-sync

## Purpose

The `dcal-sync` capability defines the `Dcal` server that backs the `dcal-lookup` tools. It subscribes to an iCal feed, parses and expands events with `Ical.Net`, persists them into a local SQLite store, and exposes an HTTP surface for sync control and health. Lookup tools query this store rather than the model reasoning over raw event lists.
## Requirements
### Requirement: Dcal syncs an iCal subscription URL into SQLite on startup
`CalendarSyncService` SHALL perform an immediate sync when `Dcal` starts, downloading the iCal URL configured via `DCAL_ICAL_URL`, parsing all events with `Ical.Net`, and persisting them to the local SQLite database. The server SHALL NOT serve lookup requests until the initial sync completes.

#### Scenario: Initial sync populates the database
- **WHEN** `Dcal` starts with a valid `DCAL_ICAL_URL`
- **THEN** events from the iCal feed are present in SQLite before the first HTTP request is served

#### Scenario: Missing DCAL_ICAL_URL fails fast at startup
- **WHEN** `Dcal` starts without `DCAL_ICAL_URL` configured
- **THEN** the process exits with a descriptive error message

### Requirement: Dcal re-syncs periodically
`CalendarSyncService` SHALL re-fetch the iCal URL and perform a full-replace sync every `DCAL_SYNC_INTERVAL_MINUTES` minutes (default 15). Each sync cycle replaces all rows in the `events` table with the current feed contents.

#### Scenario: Periodic sync updates changed events
- **WHEN** a sync cycle completes after an event was updated in the source calendar
- **THEN** the updated event fields are reflected in the SQLite store

#### Scenario: Periodic sync removes cancelled events
- **WHEN** a sync cycle completes after an event was deleted from the source calendar
- **THEN** the deleted event is no longer returned by lookup queries

### Requirement: Dcal expands recurring events over a configurable horizon
`CalendarSyncService` SHALL expand recurring event rules into individual occurrences over the window `[now, now + DCAL_RECURRENCE_HORIZON_DAYS)` (default horizon 90 days) using `Ical.Net`'s occurrence expansion, where **`now` is obtained from an injectable time source (`TimeProvider`)** — `TimeProvider.System` in production. Occurrences before `now` are excluded; each stored occurrence is a separate row with a unique synthetic UID. Sourcing `now` from `TimeProvider` makes the windowing deterministically testable (a fixed clock yields a fixed occurrence set).

#### Scenario: Weekly recurring event has multiple rows
- **WHEN** the iCal feed contains a weekly recurring event and sync completes
- **THEN** the `events` table contains one row per occurrence within `[now, now + DCAL_RECURRENCE_HORIZON_DAYS)`

#### Scenario: Occurrences outside the horizon are not stored
- **WHEN** a recurring event has an occurrence beyond `DCAL_RECURRENCE_HORIZON_DAYS`
- **THEN** that occurrence is NOT present in the `events` table

#### Scenario: Occurrences before now are not stored
- **WHEN** a recurring (or single) event has an occurrence whose start is before `now`
- **THEN** that occurrence is NOT present in the `events` table

#### Scenario: A fixed clock yields a deterministic occurrence set
- **WHEN** `CalendarSyncService` is constructed with a `TimeProvider` pinned to a fixed instant and sync completes
- **THEN** the set of stored occurrences depends only on that fixed instant and the feed, not on wall-clock time

### Requirement: POST /api/sync triggers an immediate out-of-cycle sync
The `POST /api/sync` endpoint SHALL initiate a sync immediately, independent of the background schedule, and return `204 No Content` once the sync is complete.

#### Scenario: Manual sync updates the store
- **WHEN** `POST /api/sync` is called after a new event is added to the source calendar
- **THEN** the response is 204 and the new event is queryable immediately after

### Requirement: GET /health reports sync status
The `GET /health` endpoint SHALL return `200 OK` with a JSON body containing `lastSync` (ISO-8601 timestamp of last successful sync) and `eventCount` (total rows in `events`).

#### Scenario: Health check reflects last sync time
- **WHEN** `GET /health` is called after a successful sync
- **THEN** the response contains a `lastSync` value matching the time of the most recent sync

#### Scenario: Health check reports zero events before first sync
- **WHEN** `GET /health` is called before the initial sync completes
- **THEN** `eventCount` is 0 and `lastSync` is null

### Requirement: Dcal HTTP API requires an API key by default

The `Dcal` server SHALL require a valid API key (`X-Api-Key`) on every `/api/*` endpoint (`GET /api/events/next`, `GET /api/events/upcoming`, `POST /api/sync`) regardless of whether `DCAL_API_KEY` is configured — the authentication middleware SHALL be installed unconditionally (default-deny), never gated on the presence of a configured key. A request to any `/api/*` endpoint with a missing or incorrect key SHALL be rejected with HTTP 401 before any handler logic runs. The `GET /health` endpoint SHALL remain exempt so container and reverse-proxy liveness probes work without the secret. The key SHALL be validated with a constant-time comparison.

When `DCAL_API_KEY` is unset, the server SHALL auto-generate a key on first run, persist it to a file with owner-only (`0600`) permissions, reuse that persisted key on subsequent restarts, and log only the file **path** — never the key value.

#### Scenario: Unauthenticated request is rejected even when no key is configured

- **WHEN** `Dcal` runs with `DCAL_API_KEY` unset and `GET /api/events/upcoming` is called with no `X-Api-Key` header
- **THEN** the response is HTTP 401 (the server is not open merely because no key was configured)

#### Scenario: Valid key admits the request

- **WHEN** `GET /api/events/upcoming` (or `/api/events/next`, or `POST /api/sync`) is called with a correct `X-Api-Key`
- **THEN** the request is handled normally

#### Scenario: Health requires no API key

- **WHEN** `GET /health` is called with no `X-Api-Key` header
- **THEN** the request is served (not rejected with 401)

#### Scenario: Incorrect key is rejected in constant time

- **WHEN** `GET /api/events/next` is called with an incorrect `X-Api-Key`
- **THEN** the response is HTTP 401 and the comparison uses a constant-time equality check (no early-out on the first differing byte)

#### Scenario: Auto-generated key is persisted and not logged

- **WHEN** `Dcal` starts with `DCAL_API_KEY` unset and no persisted key file present
- **THEN** it generates a key, writes it with `0600` permissions, logs only that path, and the key value appears in no log output

