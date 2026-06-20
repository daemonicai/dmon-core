## Why

`Dcal.Tests.CalendarSyncServiceTests` has three tests that fail by an off-by-one occurrence count (`TriggerSync_PopulatesDatabase` 1→0, `TriggerSync_ResyncRemovesDeletedEvent` 2→1, `TriggerSync_RecurringEvent_ExpandsOccurrences` 5→4). The tests hard-code absolute event dates (`DTSTART:20260620…`) that were future-dated when written, but `CalendarSyncService` windows occurrences from "now" (`vevent.GetOccurrences(CalDateTime.UtcNow)`), so once that wall-clock instant passes, the first occurrence is correctly dropped and the fixed assertions break. They fail on clean `main` as of 2026-06-20; the one test that uses a relative date (`DateTime.UtcNow`) still passes. This is a time-bomb that will keep recurring and currently reddens `make test` for every branch.

## What Changes

- Introduce a **`TimeProvider` seam** into `CalendarSyncService`: take a `TimeProvider` constructor parameter and source "now" (the occurrence-window lower bound and the horizon base) from `timeProvider.GetUtcNow()` instead of `CalDateTime.UtcNow` / `DateTime.UtcNow`.
- Register `TimeProvider.System` in `Dcal`'s `Program.cs` DI — **zero production behaviour change** (system clock as before).
- Update `CalendarSyncServiceTests` to inject a `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`, already pinned in `Directory.Packages.props`) pinned to a fixed instant just before the hard-coded event dates, making the assertions deterministic regardless of wall-clock.
- (Rejected alternative: merely re-dating the fixtures relative to `UtcNow` — leaves the tests time-coupled and offers no control over "now"; the `TimeProvider` seam matches the repo's existing `TimeProvider.Testing` convention.)

## Capabilities

### New Capabilities
<!-- None. -->

### Modified Capabilities
- `dcal-sync`: clarify that recurring-occurrence expansion is windowed `[now, now + DCAL_RECURRENCE_HORIZON_DAYS)` where **"now" is supplied by an injectable time source** (so occurrences in the past are excluded and behaviour is deterministically testable). No change to the production windowing behaviour — only the clock becomes injectable and the lower bound is made explicit.

## Impact

- **Code:** `services/Dcal/CalendarSyncService.cs` (ctor + `SyncAsync` now-source), `services/Dcal/Program.cs` (register `TimeProvider.System`), `test/Dcal.Tests/CalendarSyncServiceTests.cs` (inject `FakeTimeProvider`).
- **Dependencies:** `test/Dcal.Tests` gains a `Microsoft.Extensions.TimeProvider.Testing` PackageReference (version already centrally pinned — no `Directory.Packages.props` change expected).
- **No change** to the `Dcal` HTTP surface, the `dcal-lookup` tool, or any other component. Unrelated to and independent of the `graft-dmail-server` change.
