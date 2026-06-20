## Context

`CalendarSyncService.SyncAsync` (`services/Dcal/CalendarSyncService.cs`) anchors its occurrence window on the wall clock:

```csharp
CalDateTime nowCal = CalDateTime.UtcNow;
CalDateTime horizonCal = new(DateTime.UtcNow.AddDays(_recurrenceHorizonDays), CalDateTime.UtcTzId);
...
vevent.GetOccurrences(nowCal).TakeWhile(o => o.Period.StartTime < horizonCal);
```

This is correct for a "what's next" calendar — past occurrences are intentionally dropped. But three tests in `CalendarSyncServiceTests` hard-code `DTSTART:20260620…` (and `20260621…`) as if those instants are in the future; once the wall clock passes them, `GetOccurrences(now)` drops the first occurrence and the fixed `Assert.Equal` counts go off by one. `TriggerSync_HorizonBoundary_LimitsOccurrences` is immune because it builds its `DTSTART` from `DateTime.UtcNow`.

The repo already pins `Microsoft.Extensions.TimeProvider.Testing` in `Directory.Packages.props`, establishing `TimeProvider`/`FakeTimeProvider` as the house pattern for time-dependent tests.

## Goals / Non-Goals

**Goals:**
- Make the occurrence window's "now" injectable via `TimeProvider` so the tests are deterministic regardless of when they run.
- Keep production behaviour identical (system clock).
- Turn the three time-bomb tests green and keep them green permanently.

**Non-Goals:**
- No change to the windowing *policy* (still `[now, now + horizon)`), the HTTP surface, the full-replace sync strategy, or the `dcal-lookup` tool.
- No change to `CalDateTime`/`Ical.Net` usage beyond where "now" comes from.
- Not touching any other service or the `graft-dmail-server` change.

## Decisions

**`TimeProvider` constructor injection.** Add a `TimeProvider timeProvider` parameter to `CalendarSyncService` (store as a field). In `SyncAsync`, derive a single `DateTimeOffset nowUtc = _timeProvider.GetUtcNow()` and build both `nowCal` (`new CalDateTime(nowUtc.UtcDateTime, CalDateTime.UtcTzId)`) and `horizonCal` (`nowUtc.UtcDateTime.AddDays(_recurrenceHorizonDays)`) from it. Replace the `LastSync` stamp's `DateTime.UtcNow` with `nowUtc.UtcDateTime` too, so the recorded sync time is consistent with the clock used. `BackgroundService.ExecuteAsync`'s `PeriodicTimer` is left on the real timer — only the *now* used for windowing/stamping is injected (a fake periodic timer is out of scope and unnecessary for these tests).

**DI registration.** In `services/Dcal/Program.cs`, register `builder.Services.AddSingleton(TimeProvider.System)` so the constructor resolves the system clock in production. This is the only wiring change; behaviour is unchanged.

**Tests inject `FakeTimeProvider`.** Add `using Microsoft.Extensions.Time.Testing;` and a `Microsoft.Extensions.TimeProvider.Testing` PackageReference to `test/Dcal.Tests/Dcal.Tests.csproj` (version already centrally pinned). `CreateService` gains a `TimeProvider` argument; the existing call sites pass `new FakeTimeProvider(new DateTimeOffset(2026, 06, 20, 0, 0, 0, TimeSpan.Zero))` — a fixed instant **at the start of the day the fixtures use**, so every hard-coded `DTSTART` (09:00/10:00 that day and the following day) is at-or-after "now" and all occurrences are counted. `TriggerSync_HorizonBoundary_LimitsOccurrences` keeps building its feed from the injected clock's `GetUtcNow()` (replacing its `DateTime.UtcNow`) so it stays consistent.

**Pin the fixed instant just before the earliest fixture start.** Choosing `2026-06-20T00:00:00Z` (midnight) keeps all current fixtures (earliest start `09:00Z`) in-window while remaining a clear, stable anchor. If any fixture used an earlier time-of-day in future edits, the anchor moves earlier — documented in the test.

## Risks / Trade-offs

- **`CalDateTime` construction from the injected time** must use the UTC tz id (`CalDateTime.UtcTzId`) exactly as today, or `Ical.Net` comparisons could shift by the local offset. Mirror the existing construction precisely.
- **`GetOccurrences` lower-bound semantics** — `GetOccurrences(now)` is inclusive/exclusive per `Ical.Net`; the fixed clock is set to midnight (strictly before the 09:00/10:00 starts) to avoid depending on boundary inclusivity.
- **`TreatWarningsAsErrors`** is on for `Dcal.Tests`; the new `using`/package must not introduce analyzer warnings. Build the test project alone first.
- **Scope creep into the periodic timer** — explicitly avoided; only the windowing/stamp clock is injected.

## Open Questions

- None.
