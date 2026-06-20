# DEVLOG — dcal-sync-clock-seam

## Block 1.1–4.1 — TimeProvider seam (one block)

Small, tightly-coupled change: the service seam + DI registration + deterministic tests must land together to keep the suite green. Implemented directly by the orchestrator (the worker subagent has no shell access in this environment); architect step skipped because `design.md` already specified the change at code level. Reviewer (Opus) audited the diff: **APPROVE**, no blockers.

### What landed
- `services/Dcal/CalendarSyncService.cs` — added a `TimeProvider` ctor param + `_timeProvider` field. `SyncAsync` now takes a single snapshot `DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime` and builds `nowCal`, `horizonCal`, and the `LastSync` stamp from it (was two separate `CalDateTime.UtcNow`/`DateTime.UtcNow` reads). `CalDateTime.UtcTzId` construction preserved. `ExecuteAsync`'s `PeriodicTimer` left on the real timer (out of scope).
- `services/Dcal/Program.cs` — `builder.Services.AddSingleton(TimeProvider.System)` (production resolves the system clock; zero behaviour change).
- `test/Dcal.Tests/Dcal.Tests.csproj` — added `Microsoft.Extensions.TimeProvider.Testing` PackageReference (CPM pin pre-existed; no `Directory.Packages.props` change).
- `test/Dcal.Tests/CalendarSyncServiceTests.cs` — `CreateService` takes an optional `TimeProvider` defaulting to `new FakeTimeProvider(FixedNow)` where `FixedNow = 2026-06-20T00:00:00Z` (midnight — strictly before the fixtures' 09:00/10:00 starts, sidestepping `GetOccurrences` lower-bound inclusivity). The horizon test sources its `today` from the injected clock.

### Gates
- `make build` / `dotnet build test/Dcal.Tests`: 0 warnings / 0 errors.
- `Dcal.Tests`: **13/13** (was 10 pass / 3 fail).
- `env -u MEKO_API_KEY make test`: **exit 0, no failures across the whole suite** — the time-bomb is gone.
- `openspec validate dcal-sync-clock-seam --strict`: valid.

### Notes
- Reviewer flagged (non-blocking) that the `Program.cs` endpoint handlers (`/api/events/next`, `/upcoming`) and their `effectiveAfter` defaults still call `DateTime.UtcNow` directly — deliberately out of scope (the bug was purely in `CalendarSyncService` windowing).
- Standing-spec sync: the `dcal-sync` MODIFIED requirement (window `[now, now+horizon)` + injectable clock; new scenarios "Occurrences before now are not stored" and "A fixed clock yields a deterministic occurrence set") is in the change delta and must be carried into `openspec/specs/dcal-sync/spec.md` at archive time (handled automatically by `openspec archive`).
