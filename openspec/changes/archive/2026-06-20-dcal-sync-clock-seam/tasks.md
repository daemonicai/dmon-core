## 1. TimeProvider seam in the service

- [x] 1.1 Add a `TimeProvider timeProvider` constructor parameter to `CalendarSyncService` (store as a readonly field).
- [x] 1.2 In `SyncAsync`, derive `DateTimeOffset nowUtc = _timeProvider.GetUtcNow()` and build `nowCal` and `horizonCal` from `nowUtc.UtcDateTime` (preserving `CalDateTime.UtcTzId`); replace the `LastSync` stamp's `DateTime.UtcNow` with `nowUtc.UtcDateTime`. No other behaviour change.

## 2. DI registration

- [x] 2.1 Register `TimeProvider.System` as a singleton in `services/Dcal/Program.cs` so the constructor resolves the system clock in production.

## 3. Deterministic tests

- [x] 3.1 Add a `Microsoft.Extensions.TimeProvider.Testing` `PackageReference` to `test/Dcal.Tests/Dcal.Tests.csproj` (version centrally pinned; no `Directory.Packages.props` change expected).
- [x] 3.2 Update `CalendarSyncServiceTests.CreateService` to accept and pass a `TimeProvider`; inject a `FakeTimeProvider` pinned to `2026-06-20T00:00:00Z` for the fixed-date fixtures, and source `TriggerSync_HorizonBoundary_LimitsOccurrences`'s feed date from the injected clock. All four tests pass deterministically.

## 4. Gates

- [x] 4.1 Gates green: `make build` (warnings-as-errors clean), `env -u MEKO_API_KEY make test` (all `Dcal.Tests` pass + full suite), `openspec validate dcal-sync-clock-seam --strict`.
