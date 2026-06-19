## 1. Dmon.Tools.Calendar package setup

- [x] 1.1 Create `tools/Dmon.Tools.Calendar/Dmon.Tools.Calendar.csproj` targeting net10.0 with `TreatWarningsAsErrors`, `Nullable enable`, `ImplicitUsings enable`, `IsPackable=true` (+ `PackageId`/`Description`/`PackageTags`, mirroring `Dmon.Tools.Dmail`); add `PackageReference` for `Microsoft.Extensions.AI`; add `ProjectReference` to **both** `Dmon.Abstractions` **and** `Dmon.Protocol` (the latter defines `PermissionResult`, returned by `Evaluate`)
- [x] 1.2 Add `Dmon.Tools.Calendar` to `./tools.slnx` (root-level) and `./Everything.slnx`; confirm `make build` still passes

## 2. Dmon.Tools.Calendar — contracts and HTTP client

- [x] 2.1 Define `record CalendarEvent(string Uid, string Title, string? Description, string? Location, string StartUtc, string EndUtc)` as the shared DTO
- [x] 2.2 Define `interface ICalendarStore` with `CalendarEvent? FindNext(string term, string? after)` and `IReadOnlyList<CalendarEvent> ListUpcoming(int maxResults, string? after)`
- [x] 2.3 Implement `internal sealed class CalendarClient` — thin `HttpClient` wrapper over `GET /api/events/next` and `GET /api/events/upcoming`; authenticates with `X-Api-Key` header; returns `null`/empty on 404; wraps `HttpRequestException` and `TaskCanceledException` into a result string (never throws to caller)
- [x] 2.4 Implement `CalendarClient` as `ICalendarStore` — `FindNext` calls `GET /api/events/next`, `ListUpcoming` calls `GET /api/events/upcoming`

## 3. Dmon.Tools.Calendar — extension and ability provider

- [x] 3.1 Implement `sealed class CalendarExtension : IToolExtension` — parameterless constructor reads `DCAL_BASE_URL` (default `http://localhost:5280`) and `DCAL_API_KEY` from env; explicit constructor accepts `(string baseUrl, string? apiKey, HttpClient? httpClient = null)`; builds `lookup_calendar` and `list_upcoming_events` `AIFunction`s over `CalendarClient` via `DmonAIFunctionFactory.Create()` (as `Dmon.Tools.Dmail` does); `PermissionResult Evaluate(...)` returns `PermissionResult.Allow` for both tools (the enum is `{ Allow, Prompt, Deny }` — there is no `Read` value)
- [x] 3.2 Implement `sealed class CalendarAbilityProvider : IAbilityProvider` — `Scope => "personal"`; `Tools` returns the same `AIFunction`s as `CalendarExtension` (construct a shared `CalendarClient` instance)
- [x] 3.3 Implement `AddCalendarAbilities<T>()` extension on `IToolRegistration` that registers both `CalendarExtension` as `IToolExtension` and `CalendarAbilityProvider` as `IAbilityProvider`

## 4. Daemon.Calendar server setup

- [x] 4.1 Create `daemon/Daemon.Calendar/Daemon.Calendar.csproj` — ASP.NET Core minimal-API, net10.0; add `PackageReference` for `Ical.Net` and `Microsoft.Data.Sqlite`; `IsPackable=false`
- [x] 4.2 Create `./daemon.slnx` (root-level, mirroring the other per-bucket solutions `./tools.slnx`, `./core.slnx`, …) referencing `Daemon.Calendar`; add `./daemon.slnx` to `./Everything.slnx`; confirm `make build` passes

## 5. Daemon.Calendar — SQLite store

- [x] 5.1 Implement `CalendarDatabase` — creates the `events` table and `events_fts` FTS5 virtual table (schema from D3) on first use via `Microsoft.Data.Sqlite`; exposes `Upsert(IEnumerable<CalendarEvent>)`, `Clear()`, `FindNext(term, after)`, `ListUpcoming(maxResults, after)`, and `Count()`. `events_fts` is an external-content table (`content=events`) and is **not** auto-maintained: `Clear()` must also empty `events_fts`, and `Upsert(...)` must insert the corresponding `events_fts` rows (or wire FTS5 sync triggers) — otherwise `FindNext`'s `MATCH` returns nothing
- [x] 5.2 Verify `FindNext` uses `events_fts MATCH :term AND start_utc >= :after ORDER BY start_utc LIMIT 1` — the store does all matching; no event list is returned to the caller

## 6. Daemon.Calendar — iCal sync service

- [ ] 6.1 Implement `CalendarSyncService : BackgroundService` — on `ExecuteAsync` start: perform immediate sync before the server accepts requests; then loop on a `PeriodicTimer` with interval from `DCAL_SYNC_INTERVAL_MINUTES` (default 15)
- [ ] 6.2 Implement sync logic: download `DCAL_ICAL_URL` via `HttpClient`; parse with `Ical.Net`; expand recurring events up to `DCAL_RECURRENCE_HORIZON_DAYS` (default 90) days from now; call `CalendarDatabase.Clear()` then `CalendarDatabase.Upsert(occurrences)`; update `LastSync` timestamp. Persist `start_utc`/`end_utc` normalised to UTC in the **single canonical format** `yyyy-MM-ddTHH:mm:ssZ` (fixed precision) — identical to the `after` default in 7.1 — so the lexical `start_utc >= :after` comparison is correct
- [ ] 6.3 Fail fast on missing `DCAL_ICAL_URL`: validate at startup in `Program.cs` before `app.Run()`, exit with a descriptive message if absent

## 7. Daemon.Calendar — HTTP API

- [ ] 7.1 Wire `GET /api/events/next` → `CalendarDatabase.FindNext(term, after ?? <now>)` where `<now>` is `DateTime.UtcNow` formatted in the canonical `yyyy-MM-ddTHH:mm:ssZ` format (matching 6.2, **not** `"o"` — its sub-second precision would mis-sort against stored values) → return `CalendarEvent` or 404
- [ ] 7.2 Wire `GET /api/events/upcoming` → `CalendarDatabase.ListUpcoming(maxResults, after ?? now)` → return `CalendarEvent[]`
- [ ] 7.3 Wire `POST /api/sync` → trigger `CalendarSyncService` out-of-cycle sync → return 204
- [ ] 7.4 Wire `GET /health` → return `{ lastSync, eventCount }` JSON
- [ ] 7.5 Add optional `X-Api-Key` auth middleware: if `DCAL_API_KEY` env var is set, reject requests missing the matching header with 401; skip auth if env var is absent

## 8. Tests

- [ ] 8.1 Add `test/Dmon.Tools.Calendar.Tests/` xUnit project; add to solution files
- [ ] 8.2 Test `CalendarExtension` tool registration: `lookup_calendar` and `list_upcoming_events` present in `Tools`; `Evaluate(...)` returns `PermissionResult.Allow` for both
- [ ] 8.3 Test `CalendarAbilityProvider`: `Scope == "personal"`; tools present in `AbilityRegistry.ForScope("personal")`; tools absent from `AbilityRegistry.ForScope("world")`
- [ ] 8.4 Test `CalendarExtension` resilience: unreachable server returns a string error message (no exception); 404 from server returns "no event found" message
- [ ] 8.5 Add `test/Daemon.Calendar.Tests/` xUnit project; add to solution files
- [ ] 8.6 Test `CalendarDatabase.FindNext`: matching event returned; no match returns null; `after` filter excludes past events; returned fields match stored values exactly; `start_utc`/`end_utc` round-trip byte-for-byte in the canonical `yyyy-MM-ddTHH:mm:ssZ` format and the `after` lexical comparison is correct across that format
- [ ] 8.7 Test `CalendarDatabase.ListUpcoming`: chronological order; respects `maxResults`; empty result on empty store
- [ ] 8.8 Test `CalendarSyncService` sync logic with a fake iCal feed: events populated after sync; deleted events removed on re-sync; recurring events expanded within horizon; occurrences beyond horizon not stored
- [ ] 8.9 Test dual-registration invariant (D6): after `AddCalendarAbilities()`, each tool appears exactly once in the global `IToolExtension` tool list **and** in `AbilityRegistry.ForScope("personal")`, and `IAbilityProvider` tools do not leak into the global `IToolExtension` list
