# DEVLOG — gateway-session-reaping

Narrative companion to `tasks.md`. Newest block last.

## Pinned decisions (cross-block memory)

- **D2a-vs-D2b is settled → choose D2b for Block 2 (Group 2).** The task-1.1 audit enumerated every `DetachedAt` reader:
  1. `SessionReaper.cs:79` (production) — treats `null` as "connected → skip", non-null as "eligible". Does **not** depend on "was previously attached". D2a-compatible.
  2. `HeartbeatAndReaperTests.cs` — asserts `DetachedAt` non-null after detach / null while attached. Critically, **`DetachedAt_SetOnDetach_ClearedOnAttach` (`SessionHandlerTests.cs`, ~line 429)** asserts a freshly-constructed, never-attached handler has `DetachedAt == null` (`// starts with null: no connection was ever attached`). This reader **depends on the "null == never attached" distinction**, and **task 2.4 explicitly requires this test to stay green.**
  - **Conclusion:** D2a (arm `_detachedAt` at construction) would break `DetachedAt_SetOnDetach_ClearedOnAttach` — a test the change mandates stay green. So Block 2 MUST use **D2b**: a distinct never-attached / `_createdAt` clock the reaper honours, cleared on first `Attach`, reusing the idle TTL (D3). Not a blocker, no ADR needed. Block 2's worker should re-confirm but this is the answer.
- **`_detachedAt` has exactly two write sites** after Block 1: `Detach` (`SessionHandler.cs:~296`) and the new `DrainAsync` catch arm (`~609`). Both `??= _timeProvider.GetUtcNow()` inside `lock (_lock)` under `ReferenceEquals(_connection, current/connection)`. Cleared in `Attach` (`~216`, `= null`).
- **Idle TTL in the reaper test harness** = `IdleDetachedTtlMinutes = 15`.
- **Reviewer architectural note (informational, for Block 2):** `DrainAsync`'s catch clears `_connection` + arms `_detachedAt` but does not remove the connection from `_connectionIndex` — index removal stays `Detach`'s job (`SessionHandler.cs:285-286`), and the endpoint's `finally → Detach(current)` always reconciles it in production. If Block 2 ever adds a reap path that bypasses `Detach`, keep the index in mind.

---

## Block 1 — tasks 1.1–1.3 — "Drain-failure disconnect arms the grace clock" (leak #1)

**Decision D1 (implemented):** In `SessionHandler.DrainAsync`'s catch block, inside the existing `lock (_lock)` and inside the `if (ReferenceEquals(_connection, current))` guard that nulls `_connection`, added `_detachedAt ??= _timeProvider.GetUtcNow();`. A detected send/pump failure is now reap-equivalent to an orderly `Detach`: idempotent with the endpoint's subsequent no-op `finally → Detach(current)`, and a stale drainer superseded by a live re-`Attach` neither nulls nor arms (identity guard).

**Scope:** Only `SessionHandler.cs` (arm + two accuracy-only doc-comments) and `HeartbeatAndReaperTests.cs`. `SessionReaper.cs` / `SessionRegistry.cs` / `NetworkConnectionEndpoint.cs` untouched (D4 — reap path unchanged, just widened *when* a handler is eligible).

**Test:** `Reaper_DrainFailureDisconnect_ArmsGraceClock_AndReapedAfterIdleTtl` in `HeartbeatAndReaperTests.cs` + a minimal `ThrowingConnection` helper. Attaches the throwing connection, waits for the send failure, then asserts `handler.DetachedAt` is non-null **with no `Detach` call anywhere** — proving the arm happens in `DrainAsync` itself (genuine fail-without-fix guard). Then exercises the reaper: 14 min → still registered; +2 min (16 total, past the 15-min idle TTL) → reaped (`registry.TryGet` null). `SendFailure_DoesNotAdvanceCursor_AndReplaysOnReattach` stays green (re-attaches immediately, clearing `_detachedAt`).

**Gates:** `make build` clean (0 warnings, TWAE on); `dotnet test test/Dmon.Network.Tests` 210/210; full `env -u MEKO_API_KEY make test` all 20 assemblies green; `openspec validate --strict` valid. Reviewer signed off (no blockers).

---

## Block 2 — tasks 2.1–2.4 (+ Group 3 gates 3.1–3.3) — "Created-but-never-attached handlers become reapable" (leak #2, D2b)

**Decision (2.1, confirmed):** D2b, as pinned above. Re-grep confirmed the only `DetachedAt` readers are `SessionReaper.cs:79` and test assertions in `HeartbeatAndReaperTests.cs` (incl. `DetachedAt_SetOnDetach_ClearedOnAttach`, which requires `DetachedAt == null` on a never-attached handler). D2a would break it → D2b.

**Implementation (2.2):** `DetachedAt` and its two write sites left byte-for-byte unchanged. Added a distinct never-attached clock:
- `private readonly DateTimeOffset _createdAt;` — set once from `_timeProvider.GetUtcNow()` in both concrete constructors (the delegating 3-arg ctor chains via `: this(...)`).
- `public DateTimeOffset? ReapEligibleSince { get { lock(_lock) { if (_connection is not null) return null; return _detachedAt ?? _createdAt; } } }`. The `_createdAt` fallback fires **only** when `_connection == null && _detachedAt == null` — after Block 1 that state is reachable **only** by a never-attached handler (any attach sets `_connection`; any detach OR drain-failure arms `_detachedAt`). So it's a precise never-attached clock, "cleared on first `Attach`" implicitly (returns null while attached; after a later detach `_detachedAt` takes precedence). No `_attachedEver` flag needed.
- `SessionReaper.cs:79` reads `handler.ReapEligibleSince` instead of `handler.DetachedAt` (local renamed `eligibleSince`); the idle-vs-absolute-max `turnInFlight` branch is untouched — a never-attached handler is not in-flight, so the idle TTL governs (D3). No `SessionRegistry.cs` / `NetworkConnectionEndpoint.cs` change.

**Reap path unchanged (2.3, D4):** never-attached reap flows through the reaper's existing `Remove`/`StopAsync`/`DisposeAsync`; no bypass. A never-attached handler was never in `_connectionIndex`.

**Tests (2.4):** `Reaper_CreatedButNeverAttached_ReapedAfterIdleTtl` (asserts `DetachedAt == null` up front to prove the clock is `_createdAt` not `_detachedAt`; 14 min survives, +2 → 16 > 15 TTL reaps; genuine fail-without-fix) and `Reaper_AttachBeforeIdleTtl_ClearsReapClock_AndSurvives` (attach at 10 min, advance to 20 > 15 from creation, survives because attached ⇒ `ReapEligibleSince == null`). All named existing tests stay green.

**Reviewer architectural note (informational):** a never-attached handler whose core emits `turnStart` before any attach would be `IsTurnInFlight == true` and measured against `RunningTurnTtlMinutes` (absolute-max) from `_createdAt` rather than the idle TTL — consistent with the documented in-flight ⇒ hard-ceiling policy, still bounded, correct.

**Gates (Group 3, 3.1–3.3):** `make build` clean (0 warnings, TWAE on); `dotnet test test/Dmon.Network.Tests` 212/212; full `env -u MEKO_API_KEY make test` all assemblies green (Dmon.Core.Tests 606/606 etc.); `openspec validate --strict` valid. The `remote-session-gateway` spec delta was already written at propose time (all four scenarios present) — no spec edit needed at apply; 3.3 was a read-through + validate confirmation. Reviewer signed off (no blockers). Change is code-complete.
