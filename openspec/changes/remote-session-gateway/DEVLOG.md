# DEVLOG — `remote-session-gateway`

> **Status: in-flight.** Maintained while applying the change per the OpenSpec apply
> workflow (see the project's `CLAUDE.md`). Captures per-section narrative the spec
> files don't carry: decisions under uncertainty, deviations, surfaced bugs, HITL
> verifications. On archive this file moves with the change to
> `openspec/changes/archive/YYYY-MM-DD-remote-session-gateway/DEVLOG.md` and the status
> flips to **shipped** (see `/devlog freeze`).

## How to resume

- Branch: **`change/remote-session-gateway`** (created from `main`). Stay on it.
- Working tree state: **CLEAN** (Groups 1–6 committed; Group 7 not started).
- Sanity check command:
  `dotnet build Dmon.slnx -c Release && dotnet test -c Release && openspec validate remote-session-gateway --strict`
- Resume point: **§7 — Detached lifetime and liveness** (first unticked task: `7.1`). See the Section status table for what's done.
- Check the memory files listed at the bottom before briefing — `adr-014-gateway-replay-not-messages-jsonl` is load-bearing for the whole replay model.

## Section status

One row per `## N.` section in `tasks.md`. Add a row when the section commits. "Gateway tests" = `Dmon.Gateway.Tests` count after the section (full solution suite green at every commit).

| § | Section | Commit | Gateway tests | Notes |
|---|---------|--------|---------------|-------|
| 1 | Gateway host skeleton | `61ed777` | 0 | New `Dmon.Gateway` (Web SDK), `GatewayOptions` (loopback default + TTLs + max-handlers + shared-key), placeholder `/ws`, DI wiring. `IsPackable=false`. Reviewer sign-off, nits deferred to §3. |
| 2 | Session registry + handler lifecycle | `6357c61` | 3 | `SessionHandler` (single drain loop), `SessionRegistry`, `IGatewayConnection`. Reviewer caught **3 blockers** (attach-seam ordering race, unsynchronised stdin, dropped events on failed flush) → fixed → sign-off. |
| 3 | Connection-control sub-protocol | `a3dfb6b` | 31 | `gw`-discriminated frames; `/ws` handshake + byte-unchanged forwarding + ping/pong + detach-on-close. Reviewer **1 blocker** (concurrent `WebSocket.SendAsync`) → single serialized send funnel, bounded receive (4 MiB), reject binary → sign-off. Folded §1 nit (`MapGet`). |
| 4 | Event sequencing + replay | `82c0c17` | 38 | Per-session `seq` + retained in-memory seq-log + per-connection cursor = subscribe-then-replay+dedupe by construction. **Added ADR-014** (see Decisions). Reviewer **1 blocker** (`_sentSeq` lost-update race on re-attach-during-drain) → cursor advance moved under lock, gated on still-current → sign-off (reviewer reverted fix to confirm the regression test catches it). |
| 5 | Command idempotency | `73f0ae4` | 55 | Ack-by-id + per-handler admitted-id dedupe + re-ack. Reviewer **2 blockers** (non-string `id`/`gw` crashed the loop; record-before-write → silent loss + false re-ack on core-write failure) → type-tolerant parsing + admit→write→ack-on-success / compensate-on-failure → sign-off. |
| 6 | Fencing + single active writer | `7033a38` | 60 | Generation issuance (already from §3) + proactive evict (`IGatewayConnection.Abort`) + per-frame generation gate (drop+close 4409) + connection-scoped `Detach` guard. Reviewer sign-off; nit (magic-`0` fencing-off sentinel) → explicit `enforceFencing` flag, production always `true`. |

## Decisions & deviations

- **§4 — ADR-012 Decision 4 was factually unimplementable; resolved by new ADR-014.** Implementing event sequencing revealed Decision 4's premise ("seq durably backed by `messages.jsonl`") is false: the core's ADR-003 event stream is emitted to stdout by `EventEmitter` and **never persisted**; `messages.jsonl` is written only by the core (`ShortTermMemory` conversational turns + `SessionStore`), and `IMessageAppender` is single-writer-per-session. A separate gateway process can't use it as the event-replay backing without corrupting the core's log / racing its writer. **Stopped and asked the user** (the spec/ADR was wrong — CLAUDE.md §4). User chose **in-memory seq buffer + ADR amendment** (over a gateway-owned durable event log, and over the non-viable write-into-`messages.jsonl`). Wrote **ADR-014** (Amends ADR-012 Decision 4): in-session reconnect replay `(lastSeq, headSeq]` is served from the live handler's in-memory seq-indexed buffer (the handler outlives the connection); cross-restart recovery is the core loading `messages.jsonl` conversational history, with the interrupted in-flight turn lost (already an accepted ADR-012 risk). Amended `spec.md` / `design.md` D2 / `proposal.md` / `tasks.md` 4.1-4.2 / `CLAUDE.md` ADR table to match; got user OK before implementing; folded ADR-014 + amendments into commit `82c0c17`. Memory: `[[adr-014-gateway-replay-not-messages-jsonl]]`.
- **§2 — single ordered drain loop chosen over fire-and-forget flush.** Reviewer's first-pass blocker showed the original fire-and-forget buffer flush on attach would let live events overtake buffered events across the reconnect seam. Reworked to one reader task + one drain loop (the sole sender). This single ordered loop is what §4's replay and §6's fencing both build on — a load-bearing architectural choice made under review pressure.
- **§3 — single serialized send funnel.** Reviewer caught that the §3 endpoint added a second writer (`attached`/`pong`) to the same socket as §2's drain loop → illegal concurrent `WebSocket.SendAsync`. All gateway→client bytes now funnel through one `SemaphoreSlim`-serialized `WebSocketGatewayConnection.SendAsync`. §6's eviction and §7's heartbeat must use this same funnel, not the raw socket.
- **§5 — H1, false-ack-on-failure fixed rather than accepted-as-risk.** Record-before-write is correct for the concurrent-duplicate case but, on a core-write failure, would silently drop the command AND send a false re-ack on resend — defeating ADR-012 Decision 5. Chose the higher-bar fix (compensate admission on failure + ack only after a successful write) over recording it as an accepted V1 risk.
- **§5/§6 nits — input hardening + explicit fencing flag.** Hardened both control-frame parsers (`GetCommandId`/`GetGwDiscriminator`) to read top-level fields type-tolerantly (untrusted edge); replaced §6's magic-`0` "fencing off" with an explicit `enforceFencing` flag (production always enforces).

## Human-in-the-loop verifications

None required so far (Groups 1–6 are all covered by automated gates: build + full test suite + `openspec validate --strict`). Group 11.4 (Tailscale deployment docs) may surface a manual verification when reached.

## Open follow-ups / known gaps (after this change lands — NOT in scope here)

- **Event-stream durability across a gateway restart:** explicitly NOT provided (ADR-014). An interrupted in-flight turn is lost on restart; conversational continuity comes from the core loading `messages.jsonl`. Couples to any future core turn-persistence work. Memory: `[[adr-014-gateway-replay-not-messages-jsonl]]`, `[[followup-turn-persistence-across-restart]]`, `[[shortterm-memory-jsonl-double-write]]`.
- **Replay bound / compaction (ADR-012 Open Question D):** the in-memory seq buffer is unbounded for the life of the handler (V1 accepted, design Risks). The retention ceiling + truncation-signal protocol is deferred. Memory: `[[adr-014-gateway-replay-not-messages-jsonl]]`.
- **Core-write-failure close reuses `ProtocolViolationFrameException` (status 4500):** acceptable for §5, flagged to revisit when real core-death/reaping handling lands in §7.

## Memory files (indexed by `~/.claude/projects/-Users-emmz-github-emmz-dmon-core/memory/MEMORY.md`)

- `adr-014-gateway-replay-not-messages-jsonl` — gateway event replay is an in-memory seq buffer, NOT `messages.jsonl`; ADR-014 amends ADR-012 Decision 4. **Load-bearing for §4 onward.**
- `followup-turn-persistence-across-restart` — conversation history lost on `/reload`; needs a separate core turn-persistence change (couples to the restart-durability gap above).
- `shortterm-memory-jsonl-double-write` — `ShortTermMemory` is the sole writer of turn lines to `messages.jsonl`; reconcile with future core turn-persistence.

## Resume point

> **Currently at §7.1 — `ping`/`pong` heartbeats.** Groups 1–6 are committed and green (Gateway 60/60, full suite passing, `validate --strict` clean). §7 (Detached lifetime and liveness) introduces background timers/reaping: heartbeat interval + missed-beat → detected-disconnect (7.1); detached grace timer + idle-TTL reap (7.2); in-flight-turn retention bounded by absolute max + concurrent-handler cap (7.3). The next worker brief needs: an "is a turn in flight?" signal for the handler (derived from the core's event stream — agentStart/turn boundaries), the TTL/cap values already on `GatewayOptions`, and the §6 carry-forwards (reaper must use `Detach(connection)` with the identity guard; preserve `Abort`'s "don't dispose shared resources" contract so the send semaphore isn't double-disposed).
