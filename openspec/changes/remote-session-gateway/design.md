## Context

`dmoncore` speaks ADR-003 JSONL over stdio and today runs only as a local child of `Dmon.Terminal`, spawned via `Dmon.Runtime`'s `CoreProcessManager` (ADR-011). ADR-012 (Accepted) defines a single-tenant remote gateway: a personal iOS client reaches one user's sessions over Tailscale. The hard constraint is mobile connectivity — carrier NAT idle-timeouts, WiFi↔cellular handoff, and iOS backgrounding mean the connection drops routinely, so the **session must be decoupled from the connection** and resumable. The stdio protocol is already a full-duplex ordered stream; a WebSocket is its lowest-impedance network isomorph, and the resilience layer above it is where the real work is. This change builds the `agent-profiles` change on top of (the `profile` chosen at session creation).

## Goals / Non-Goals

**Goals:**
- A `Dmon.Gateway` host that forwards ADR-003 JSON over a WebSocket near-verbatim, with the wire contract unchanged.
- Connection-decoupled `SessionHandler`s that outlive connections, with seq'd event replay, command dedup, and generation fencing so reconnects resume correctly.
- Running-turn-aware detached lifetime and heartbeat liveness tuned for mobile.
- Tailscale as the auth/encryption boundary; gateway on loopback; optional shared key.

**Non-Goals:**
- Multi-instance / scale-out backplane (single instance, in-process registry).
- Simultaneous read-only multi-observer attach (ADR-012 Open Question A — deferred).
- Replay snapshot/compaction (Open Question D — deferred).
- The iOS app, APNs push, and `libtailscale` embedding (client-side / product concerns).
- Any change to the ADR-003 wire shapes or the core's behaviour.

## Decisions

**D1 — WebSocket, not gRPC/SSE/SignalR/raw-TCP.** Settled in ADR-012 (Alternatives): WebSocket preserves wire-order and TCP flow control on the command direction, has first-class dependency-free Swift support, and forwards the existing JSON near-verbatim. Resilience lives in a control sub-protocol *around* ADR-003, not in the core protocol.

**D2 — Sequencing and replay are a gateway-layer in-memory `seq` buffer (ADR-014).** The gateway assigns per-session `seq` to every server→client event and retains them in the **live handler's in-memory `seq`-indexed buffer**. Reattach uses **subscribe-then-replay**: subscribe live into a buffer, replay `(lastSeq, headSeq]` from the in-memory buffer, flush the live buffer deduping by `seq`. The core's `messages.jsonl` is **not** the event-stream backing — it holds conversational turns written only by the core (`EventEmitter` streams events to stdout, never to disk; `IMessageAppender` is single-writer-per-session, core-owned). In-session connection-drop replay is served from memory (the handler outlives the connection); cross-restart conversational recovery is the core loading `messages.jsonl` on session load, with the interrupted turn lost (an accepted ADR-012 risk). *This corrects ADR-012 Decision 4's "durably backed by `messages.jsonl`" premise — see ADR-014. Alternative — a gateway-owned durable event log — deferred: pulls Open Question D (compaction/bound) into V1 to buy durability for a case already accepted as lost. Alternative — have the core emit `seq` — rejected: keeps ADR-003 and the core untouched; sequencing is a transport concern.*

**D3 — Single active writer via generation fencing; evict-old.** Each attach bumps a generation; frames from an older generation are dropped and the socket closed; a new attach evicts the prior connection. This is the correct default for aggressive mobile reconnect, where the old socket is frequently not yet known-dead when the new one arrives (ADR-012 Decision 6).

**D4 — Detached lifetime is running-turn-aware (ADR-012 Decision 7).** Idle-detached handlers reap after a configurable idle TTL (floor sized for backgrounding/handoff — minutes); in-flight turns are retained until completion, bounded by an absolute max; a concurrent-handler cap is the resource ceiling. All values are config.

**D5 — Auth is delegated to Tailscale (ADR-012 Decision 12).** Bind loopback; `tailscale serve` fronts the gateway with a MagicDNS Let's Encrypt cert; device identity/revocation/encryption are Tailscale's. An optional single pre-shared key on the upgrade is defense-in-depth — no per-device token store or pairing is built.

**D6 — Reuse `CoreProcessManager`; the gateway is a thin host.** `Dmon.Runtime` carries no console dependency (ADR-011 D7), so the gateway inherits the same core bootstrap as the terminal host. The gateway adds session multiplexing, the control sub-protocol, and Tailscale-fronted hosting — not new core machinery.

## Risks / Trade-offs

- **[Server crash loses the in-flight turn]** → Accepted for V1: handlers and cores die with the process; reconnect replays history from `messages.jsonl` and starts a fresh handler, but an interrupted turn is gone. Document it; it rhymes with the existing `/reload`-loses-history gap and should be decided alongside any core turn-persistence work, not twice.
- **[Backgrounded app cannot be pushed a completed turn]** → No transport solves this; APNs (out of scope here) is the product dependency, paired with resume-on-foreground via `lastSeq`. The detached-TTL (D4) bounds how long a backgrounded user has to return.
- **[Replay grows unbounded for a long-absent client]** → V1 replays the full `(lastSeq, headSeq]` range; compaction is deferred (Open Question D). Note the cost rather than silently truncating.
- **[Two-writer race during the eviction window]** → The generation check MUST gate *every* inbound frame (not just attach), so a fenced connection's in-flight frames are rejected even if they arrive after the new attach.
- **[Parked permission request leaks if never resumed]** → A parked request is bounded by the handler TTL (D4); when the handler is reaped, the parked request is abandoned with the turn, not left dangling.
- **[Gateway accidentally exposed publicly]** → Loopback-by-default and explicit-opt-in to any other bind address; document that `tailscale serve`, not a public bind, is the exposure path.
