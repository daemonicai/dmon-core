# ADR-014: Gateway Event Replay — In-Memory Seq Buffer, Not `messages.jsonl`

**Date:** 2026-05-31
**Status:** Accepted
**Amends:** ADR-012 (Decision 4; the ADR-004 relationship note; the second Consequence)

## Context

ADR-012 Decision 4 states the gateway assigns a monotonic per-session `seq` to every server→client event "**durably backed by the session's append-only `messages.jsonl` (ADR-004)**", and its relationship note adds that "the session directory and append-only `messages.jsonl` are the durable backing for event sequencing and replay … the gateway adds a per-session `seq`, sourced from or aligned to the log."

Implementing the `remote-session-gateway` change revealed this rests on a false premise about what `messages.jsonl` contains:

- The core's ADR-003 **event stream** (`message`/`messageDelta`, `tool.call`, `tool.result`, `permission.request`, `ui.input`, …) is written by `EventEmitter` (`src/Dmon.Core/Rpc/EventEmitter.cs`) to **stdout only**. It is **never persisted**.
- `messages.jsonl` is written **solely by the core process** — by `ShortTermMemory` (`src/Dmon.Memory/Index/ShortTermMemory.cs`), which appends *conversational turn* lines (`{entryId, timestamp, role, text, scope}`), and by `SessionStore` (create / fork / compaction marker). It holds the **conversation**, not the event stream.
- `IMessageAppender` is explicitly documented **"not safe for concurrent calls to the same session — callers must serialise writes externally"**, and its sole caller is the in-process core.

So the event stream a reconnecting client must replay is **not in `messages.jsonl`**, and the gateway — a *separate process* spawning the core — cannot make it the durable backing without (a) writing event lines the core's readers (`ReadMessagesAsync`, the `ShortTermMemory` index, compaction) do not understand, (b) racing the core's writer on a single file, and (c) breaking `ShortTermMemory`/compaction invariants. ADR-012 Decision 4's literal premise is unimplementable as written.

ADR-012 is nonetheless internally rescuable: its Consequences already say a server crash "**loses the in-flight turn but not the session** … reconnect replays history from disk and starts a fresh handler, but an interrupted turn is gone", and the relationship note hedges `seq` is "sourced from **or aligned to**" the log. The coherent model separates two different durability needs that Decision 4 conflated.

## Decision

1. **Two distinct resume concerns, two distinct mechanisms.**
   - **In-session resume (connection drop, handler still alive — the dominant mobile case).** The gateway assigns a monotonic per-session `seq` to every server→client event as it flows out and retains the recent events in the **live handler's in-memory, `seq`-indexed buffer**. `headSeq` is the highest assigned `seq`. On reattach the handler subscribes to the live stream first, replays `(lastSeq, headSeq]` **from this in-memory buffer**, then flushes the live buffer deduping by `seq`. No disk is involved, and none is needed: the handler **outlives the connection** (ADR-012 Decision 2), so its buffer is present for every reconnect that does not cross a process restart.
   - **Cross-restart recovery (handler/core gone).** The durable resume cursor is the core's existing `messages.jsonl` **conversational history**, read by the core on session load — *not* the gateway's event buffer. A fresh handler/core rebuilds the conversation from `messages.jsonl`; the **interrupted in-flight turn is lost** (ADR-012 Consequences, unchanged). Event-stream durability across a gateway restart is **explicitly not provided** in V1.

2. **`seq` is gateway-assigned, gateway-local, and never written to `messages.jsonl`.** The core stays byte-for-byte unchanged (ADR-012 Decision 3 / ADR-003 untouched). This corrects Decision 4's "durably backed by `messages.jsonl`": the gateway's event sequencing is an in-memory transport concern; `messages.jsonl` remains the core's conversational store, written only by the core.

3. **The in-memory buffer is bounded; this is where Open Question D bites.** The retained window is bounded by the handler's detached TTL (ADR-012 Decision 7) and a retention ceiling. A client absent beyond the retained window receives a **defined truncation signal** rather than silent loss. V1 may retain the full in-session `(0, headSeq]` range for a live handler subject to that ceiling; a snapshot/compaction story remains deferred (ADR-012 Open Question D), now understood to govern the in-memory buffer's size as well as on-the-wire replay length.

## Consequences

- **The core and `messages.jsonl` are genuinely untouched.** No cross-process write race, no foreign lines in the conversational log, no violation of `ShortTermMemory`/compaction invariants or the single-writer contract of `IMessageAppender`.
- **The dominant case is fully covered without disk.** Routine cellular drops — backgrounding, handoff, NAT timeout — reattach to the live handler and replay `(lastSeq, headSeq]` from memory.
- **Event-stream durability across a gateway restart is not provided, and that is stated plainly.** An interrupted turn is lost on restart (already an accepted ADR-012 risk); conversational continuity afterward comes from the core loading `messages.jsonl` on session load, then a fresh handler. This stays coupled to any future core turn-persistence work rather than being solved twice.
- **The buffer is a bounded memory cost,** scoped to live handlers and capped by the detached TTL plus a retention ceiling (Decision 3 / Open Question D).
- **No ADR is fully superseded.** ADR-012's transport, lifetime, fencing, auth, and permission-parking decisions stand; only Decision 4's *durability backing* is corrected.

## Alternatives

- **Gateway writes its own durable per-session event log** (a separate append-only file in the ADR-004 session directory, distinct from `messages.jsonl`). Gives true event-stream durability across a restart. Rejected for V1: it pulls the deferred compaction / unbounded-growth concern (ADR-012 Open Question D) forward into V1, adds a second on-disk format and its own fork/compaction story, and buys durability for a case ADR-012 already accepts losing (the in-flight turn on crash). Revisit if/when core turn-persistence lands and "resume a turn across a server restart" becomes a real requirement.
- **Write events into the core's `messages.jsonl`.** Rejected: corrupts the conversational log the core reads, races the core's writer on one file, and breaks the `ShortTermMemory` index and compaction. Non-viable.
- **Have the core emit `seq` / persist events.** Rejected for the same reason ADR-012 rejected it: keeps ADR-003 and the core untouched; sequencing is a transport concern owned by the gateway.

## Relationship to other ADRs

- **ADR-012** — amends Decision 4's durability backing (in-memory `seq` buffer for in-session replay, not `messages.jsonl`) and clarifies the second Consequence ("the resume cursor" = the core's conversational history for cross-restart recovery; in-session event replay is the in-memory buffer). Decisions 1–3 and 5–12 are unchanged. The buffer living with the handler aligns with Decision 7 (it is reaped with the handler).
- **ADR-004** — `messages.jsonl` remains the core's conversational durable store, written **only by the core**; the gateway never writes it. Cross-restart conversational recovery uses it via session load; in-session event replay does not touch it.
- **ADR-003** — unchanged. `seq` is carried on the gateway's `attached` control frame, not on ADR-003 event shapes.
