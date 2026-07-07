## Context

`Dmon.Network` runs one `SessionHandler` per live session; each holds a child `dmoncore` process (`_coreSession`) and an unbounded per-session event-replay buffer (`_seqLog`, ADR-014). A background `SessionReaper` (`SessionReaper.cs`) periodically scans handlers and tears down those detached longer than a TTL. Reapability is gated **only** by `SessionHandler.DetachedAt` (`SessionReaper.cs:79-81`: `if (detachedAt is null) continue;`); `IsTurnInFlight` selects which TTL applies (idle vs. absolute-max) but never makes a `null`-clock handler reapable.

`_detachedAt` is written in exactly two places today: set in `Detach` (`SessionHandler.cs:295`, `_detachedAt ??= now`) **but only inside** the identity guard `if (ReferenceEquals(_connection, connection))` (`:289`); cleared in `Attach` (`:216`, `_detachedAt = null`). Both leak paths (proposal) end with `_detachedAt == null` on a handler that will never be attached again, so the reaper skips it forever.

ADR-012 D7/D8 state the grace timer "starts on detected disconnect"; ADR-014 states the buffer "is reaped with the handler." The bugs are the code failing to honour that intent, not a spec gap.

## Goals / Non-Goals

**Goals:**
- Any **detected** disconnect (orderly `Detach` **or** a drain/send failure that clears `_connection`) arms the grace clock, so the handler is reaped under the normal running-turn-aware TTL.
- A **created-but-never-attached** handler is reapable after a grace TTL and is cleared on first `Attach`.
- Reap still tears down the core process and buffer exactly as the existing idle-reap path does.
- Regression tests for both leak paths; all existing reaper/heartbeat/detach tests stay green.

**Non-Goals:**
- No change to the wire protocol, the `created`→`attach` client contract, RPC framing, or public verbs.
- No change to TTL values or the reaper's scan cadence (reuse the existing idle/absolute-max TTLs).
- No new ADR (unless implementation surfaces a genuine ADR contradiction → stop-and-ask).
- Not touching the unrelated `runtime-rpc-write-serialization` or other audit findings.

## Decisions

**D1 — Arm `_detachedAt` on the drain-failure path.** In `DrainAsync`'s catch block, inside the existing `lock (_lock)`, when the handler clears its own current connection (`ReferenceEquals(_connection, current)`), also arm the grace clock: `_detachedAt ??= _timeProvider.GetUtcNow();` in the **same** critical section that nulls `_connection`. Using `??=` keeps it idempotent with the endpoint's subsequent no-op `Detach` and with any prior arming. Because the write is guarded by the same `ReferenceEquals(_connection, current)` check that already gates the null, a concurrent re-`Attach` that installed a newer connection is not disturbed (the stale drainer neither nulls nor arms). This makes a detected send/pump failure behave identically to an orderly detach for reaping purposes.

**D2 — Reap created-but-never-attached handlers via a "never-attached since" clock.** Give the handler a notion of "registered but not yet attached" that the reaper honours. Two candidate shapes:
- **(a) Reuse `_detachedAt`, armed at construction.** Set `_detachedAt = now` when the handler is constructed/registered (never-attached ⇒ treated as detached-from-birth); `Attach` already clears it. Minimal new state; the reaper needs no change. Risk: any code that reads `DetachedAt` as "was once attached then detached" would misread a never-attached handler — audit `DetachedAt` readers before choosing this.
- **(b) A distinct `_createdAt`/`_attachedEver` gate.** The reaper additionally reaps a handler that has never been attached and whose `_createdAt` age exceeds the grace TTL. More explicit, no semantic overload of `_detachedAt`, but touches `SessionReaper` and the handler's public surface.

**Chosen: (a) if no `DetachedAt` reader depends on "was previously attached"; otherwise (b).** The worker MUST check all `DetachedAt` readers (reaper, tests, any status/telemetry) during investigation and pick accordingly; record the choice in the DEVLOG. Either way: the never-attached clock is armed at create/register time and cleared on first `Attach`, and the reaper's in-flight-turn logic still applies (a never-attached handler has no turn in flight, so the idle TTL governs).

**D3 — Grace TTL for never-attached = the existing idle TTL.** Do not introduce a new timeout; a created-but-never-attached client that never sends `attach` within the idle grace window is treated as an abandoned detach. Keep it configurable only insofar as the idle TTL already is.

**D4 — Reap path unchanged.** Reaping continues to call the handler's existing `StopAsync`/`DisposeAsync` teardown (`SessionHandler.cs:396-421`) so the core process and `_seqLog` are released the same way as an idle-detached reap; this change only widens *when* a handler is eligible, not *how* it is torn down.

**D5 — Spec delta.** Add scenarios to `remote-session-gateway` covering (a) a drain/send-failure with no re-attach becomes reapable, and (b) a created-then-never-attached handler becomes reapable; tighten the heartbeat/detached-lifetime requirement wording to "every detected disconnect arms the grace timer."

## Risks / Trade-offs

- **Reaping a session mid-reconnect.** If a client's connection drops and it reconnects within the idle TTL, the reaper must not have already torn it down. Mitigation: the drain-failure path arms the **same** grace clock as an orderly detach, so the reconnect window is identical to today's detach→reattach window (well within the idle TTL); D1 changes nothing about the TTL, only ensures the clock is armed at all. Re-`Attach` clears the clock as it does today.
- **Semantic overload of `_detachedAt` (D2a).** Arming it at construction means "detached" now also means "never attached." Mitigated by auditing all readers first; if any reader distinguishes the two, fall back to D2b. Documented in DEVLOG.
- **Concurrency.** The arm-on-drain write shares the handler's `_lock` with `Attach`/`Detach`/`DrainAsync`; keep it strictly inside the existing critical sections and rely on the same `ReferenceEquals` identity guards so a stale drainer never clobbers a live re-attach. TimeProvider (`_timeProvider`) supplies the timestamp so the reaper tests remain deterministic with `FakeTimeProvider`.
- **Test determinism.** Reuse the existing `FakeTimeProvider`-driven reaper test harness (`HeartbeatAndReaperTests.cs`); advance the clock past the idle TTL to assert reap for both new paths.

## Open Questions

None blocking. The only decision the worker must settle during investigation is D2a-vs-D2b (choose by auditing `DetachedAt` readers); both satisfy the spec and neither needs an ADR.
