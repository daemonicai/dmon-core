## 1. Drain-failure path arms the grace clock (leak #1)

- [x] 1.1 Investigate `SessionHandler` (`frontends/Dmon.Network/Sessions/SessionHandler.cs`): confirm the `_detachedAt` write sites (`Detach` ~line 295 under the `ReferenceEquals(_connection, connection)` guard ~289; cleared in `Attach` ~216), the `DrainAsync` catch block (~595-605) that nulls `_connection` without arming `_detachedAt`, and the `DetachedAt` property (~139-146). Enumerate ALL readers of `DetachedAt` (reaper, tests, any status/telemetry) — this audit decides D2's shape in task 2.
- [x] 1.2 In `DrainAsync`'s catch block, inside the existing `lock (_lock)` and under the same `ReferenceEquals(_connection, current)` guard that nulls `_connection`, arm the grace clock idempotently: `_detachedAt ??= _timeProvider.GetUtcNow();`. A stale drainer whose connection was already replaced by a live re-`Attach` must neither null nor arm (identity guard already ensures this). This makes a detected send/pump failure reap-equivalent to an orderly detach.
- [x] 1.3 Add a regression test in `test/Dmon.Network.Tests` (extend `HeartbeatAndReaperTests.cs` and/or `SessionHandlerTests.cs`): drive a drain/send failure that clears the connection with NO reattach, advance the `FakeTimeProvider` past the idle TTL, and assert the reaper reaps the handler (core terminated, removed from registry). Confirm the existing `SendFailure_DoesNotAdvanceCursor_AndReplaysOnReattach` test stays green.

## 2. Created-but-never-attached handlers become reapable (leak #2)

- [x] 2.1 Choose D2a vs D2b from `design.md` based on the task-1.1 audit of `DetachedAt` readers: **D2a** (arm `_detachedAt` at construction/registration, cleared on first `Attach`, reaper unchanged) if no reader depends on "was previously attached then detached"; otherwise **D2b** (a distinct never-attached clock the reaper honours). Record the choice and why in `DEVLOG.md`.
- [x] 2.2 Implement the chosen shape in `SessionHandler` (and `SessionReaper.cs`/`SessionRegistry.cs` only if D2b needs it) so a created-but-never-attached handler is reapable after the idle TTL and the clock is cleared on the first successful `Attach`. Reuse the existing idle TTL (no new timeout). A never-attached handler has no turn in flight, so idle-TTL governs.
- [x] 2.3 Confirm the reap path is unchanged: reaping a never-attached (or drain-failed) handler tears down the core process and `_seqLog` via the existing `StopAsync`/`DisposeAsync` teardown, identically to an idle-detached reap.
- [x] 2.4 Add a regression test: create/register a handler, never attach, advance the clock past the idle TTL, assert reap (core terminated, deregistered). Add a companion test that attaching before the TTL clears the reap clock and the handler survives. Keep all existing reaper/detach tests (`Idle detached handler reaped`, `In-flight turn survives`, cap, `DetachedAt_SetOnDetach_ClearedOnAttach`) green.

## 3. Gates and spec alignment

- [x] 3.1 `make build` clean (TreatWarningsAsErrors on).
- [x] 3.2 `env -u MEKO_API_KEY dotnet test test/Dmon.Network.Tests` green (new tests + all existing), then a single full `env -u MEKO_API_KEY make test` green (pkill stale `Everything.slnx` testhost first).
- [x] 3.3 `openspec validate gateway-session-reaping --strict` passes; the `remote-session-gateway` MODIFIED delta (every-detected-disconnect-arms + created-never-attached-reapable, with the added scenarios) matches the implemented behavior.
