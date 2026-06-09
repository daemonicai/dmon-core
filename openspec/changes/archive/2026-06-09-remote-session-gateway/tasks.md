## 1. Gateway host skeleton

- [x] 1.1 Create `src/Dmon.Gateway` (ASP.NET Core, Kestrel) with a `ProjectReference` to `Dmon.Runtime`; `IsPackable` per the repo's packaging rules.
- [x] 1.2 Bind to **loopback by default**; expose bind address, TTLs, max-handlers, and optional shared-key via gateway config.
- [x] 1.3 Add a WebSocket endpoint (`app.UseWebSockets()`) and the DI wiring (session registry, `CoreProcessManager`, profile resolver).

## 2. Session registry and handler lifecycle

- [x] 2.1 Implement `SessionHandler` owning one `dmoncore` process spawned via `CoreProcessManager` (ADR-011), keyed by `sessionId`.
- [x] 2.2 Implement the in-memory session registry (`ConcurrentDictionary<sessionId, SessionHandler>`).
- [x] 2.3 Bind the stdio pump to the handler (client frames → core stdin; core stdout → attached connection or durable buffer); implement attach/detach so the handler outlives a dropped connection.

## 3. Connection-control sub-protocol

- [x] 3.1 Define `attach` / `attached` / `ack` / `ping` / `pong` frames, distinguishable from and additive to ADR-003 command/event frames.
- [x] 3.2 Implement the attach handshake: accept `attach {sessionId, lastSeq}`, reply `attached {generation, headSeq}`.
- [x] 3.3 Verify ADR-003 command/event JSON is forwarded byte-unchanged (no reshaping at the gateway).

## 4. Event sequencing and replay

- [x] 4.1 Assign a monotonic per-session `seq` to every server→client event, retained in the live handler's in-memory `seq`-indexed buffer (ADR-014; not `messages.jsonl`); track `headSeq`.
- [x] 4.2 On attach, replay `(lastSeq, headSeq]` from the in-memory buffer then resume live.
- [x] 4.3 Implement subscribe-then-replay with dedupe by `seq` so events arriving mid-replay are delivered exactly once.

## 5. Command idempotency

- [x] 5.1 Ack each accepted command by its ADR-003 `id`.
- [x] 5.2 Dedupe resent commands by `id` so a command delivered before a drop is not executed twice; re-ack a duplicate.

## 6. Fencing and single active writer

- [x] 6.1 Issue a strictly increasing `generation` on each attach.
- [x] 6.2 Gate **every** inbound frame on generation; drop frames from and close any connection older than the current generation.
- [x] 6.3 Evict (fence + close) the prior connection when a new attach succeeds for a live handler.

## 7. Detached lifetime and liveness

- [x] 7.1 Implement `ping`/`pong` heartbeats on a configurable interval; treat missed heartbeats as a detected disconnect.
- [x] 7.2 Start the detached grace timer on detected disconnect; reap idle-detached handlers after the idle TTL.
- [x] 7.3 Retain a detached handler with a turn in flight until the turn completes, bounded by the absolute max; enforce the concurrent-handler cap.

## 8. Permission parking while detached

- [x] 8.1 While detached, hold permission requests unresolved — neither auto-approve nor auto-deny.
- [x] 8.2 Deliver a parked request to the client on reattach; abandon it with the turn if the handler is reaped first.

## 9. Tailscale-fronted auth

- [x] 9.1 Enforce loopback-by-default; require explicit config to bind any other interface; never bind a public NIC.
- [x] 9.2 When a shared key is configured, validate `Authorization: Bearer <key>` on the WebSocket upgrade and reject mismatches with HTTP 401 before opening a socket.

## 10. Profile-selecting session creation

> **DEFERRED to a follow-up change (user decision, 2026-05-31).** Per-session profile selection is not wired into the core RPC protocol — `TurnHandler` resolves the profile with `requestedProfile: null` and a standing comment notes it is unwired; `session.create`/`CoreLauncher` carry no profile. Making "the gateway spawns a handler whose core runs under that profile" true requires changes to `Dmon.Protocol` (profile on the create surface) and `Dmon.Core` (thread `requestedProfile` into `AgentProfileContext.EnsureResolvedAsync`) — outside the `remote-session-gateway` (gateway-only) scope. Split into its own change spanning core + gateway. The `MaxConcurrentHandlers` cap primitive (`SessionRegistry.TryRegister`, §7) and the attach-only flow are ready for that change to build on. See DEVLOG "Decisions & deviations".

- [ ] 10.1 Implement session creation: allocate `sessionId`, resolve the agent profile via the `agent-profiles` resolver, provision the per-session storage dir (ADR-004), spawn the handler. *(Depends on the `agent-profiles` change.)* — **DEFERRED (see note above)**
- [ ] 10.2 Fail session creation with an actionable error when the requested profile is unknown; spawn no handler. — **DEFERRED (see note above)**

## 11. Tests and deployment docs

- [x] 11.1 Test: drop-and-reattach replays missed events in order with no duplicate across the seam.
- [x] 11.2 Test: resent command is deduped by `id`; in-flight-turn handler survives detach while idle handler reaps at TTL.
- [x] 11.3 Test: a new attach evicts the prior connection and fenced frames are rejected; shared-key mismatch yields HTTP 401.
- [x] 11.4 Document deployment: `tailscale serve` fronting the loopback gateway, MagicDNS cert, and a tailnet ACL restricting the iOS device to the gateway port.
