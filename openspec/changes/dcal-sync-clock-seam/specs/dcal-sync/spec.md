## MODIFIED Requirements

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
