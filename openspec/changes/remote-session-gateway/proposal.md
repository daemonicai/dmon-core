## Why

`dmoncore` is reachable only as a local stdio child of `Dmon.Terminal`. ADR-012 (Accepted) brings the deferred "remote agent execution" item into scope as a *single-tenant* capability: expose one user's `dmoncore` sessions to a personal iOS client over a network boundary, reached only over Tailscale. Mobile connections drop routinely (carrier NAT, WiFi↔cellular handoff, iOS backgrounding), so the server must hold a long-lived handler that **outlives any single connection** and lets a reconnect re-attach and resume. This change implements that gateway.

## What Changes

- Add a new ASP.NET Core host, **`Dmon.Gateway`**, exposing a **raw WebSocket** endpoint that forwards ADR-003 command/event JSON to and from a `dmoncore` process near-verbatim. It reuses `Dmon.Runtime`'s `CoreProcessManager` (ADR-011) for core discovery/spawn/lifecycle.
- Introduce a **`SessionHandler`** that owns one `dmoncore` process per `sessionId`, held in an in-memory **session registry**. A WebSocket **attaches** on connect and **detaches** on drop; the handler persists across detach. The stdio pump binds to the handler, not the connection.
- Add a **connection-control sub-protocol** layered around the unchanged ADR-003 messages: `attach` (`sessionId`, `lastSeq`), `attached` (`generation`, `headSeq`), `ack` (command `id`), `ping`/`pong`. **The ADR-003 wire contract does not change.**
- **Resume on reconnect:** assign a monotonic per-session **`seq`** to every server→client event (durably backed by `messages.jsonl`, ADR-004); a reattaching client sends `lastSeq` and the handler replays `(lastSeq, headSeq]` then resumes live (subscribe-then-replay to close the seam).
- **Command idempotency:** the handler acks commands by `id`; a reattaching client resends unacked commands; the handler dedups by `id`.
- **Stale-connection fencing + single active writer:** each attach issues a monotonically increasing **generation**; frames from an older generation are dropped and that socket closed. A new attach **evicts** the prior connection.
- **Running-turn-aware detached lifetime:** on detected detach, an idle handler is reaped after a configurable TTL; a handler with a turn in flight is kept alive until the turn completes, bounded by an absolute maximum; a hard cap limits concurrent live handlers.
- **Liveness:** application-level `ping`/`pong` heartbeats beat carrier NAT idle-timeouts and detect dead/ghost sockets; the detached-TTL clock starts on *detected* disconnect.
- **Permission parking:** while detached, a turn that reaches an ADR-006 permission gate **parks** (neither auto-approve nor auto-deny) and resumes on reattach or times out under the handler TTL.
- **Auth via Tailscale:** the gateway binds **loopback**; `tailscale serve` fronts it with a Let's Encrypt cert for the MagicDNS name. Device identity/revocation/encryption are Tailscale's; an **optional single shared key** on the WebSocket upgrade adds defense-in-depth.
- **Session creation** allocates the `sessionId`, selects an **agent profile** (ADR-013 / the `agent-profiles` change — persona, asset dir, permission mode), provisions the per-session storage dir, and spawns the handler.
- **Single server instance for V1** — `sessionId → handler` is in-process affinity; no backplane.
- Out of scope: multi-instance/scale-out backplane; simultaneous read-only multi-observer attach; replay compaction; the iOS app; APNs push (a noted product dependency, not built here); embedding `libtailscale`.

## Capabilities

### New Capabilities
- `remote-session-gateway`: the `Dmon.Gateway` host, WebSocket transport and near-verbatim JSON forwarding, the `SessionHandler`/session-registry lifecycle, the connection-control sub-protocol, event sequencing + replay-on-reattach, command ack/dedup, generation fencing + evict-old, running-turn-aware detached TTL, heartbeat liveness, detached permission parking, Tailscale-fronted auth with an optional shared key, and profile-selecting session creation.

### Modified Capabilities
<!-- None. The gateway consumes session-storage (ADR-004), permission-model, and agent-profiles without changing their requirements; the ADR-003 wire contract is unchanged. -->

## Impact

- **Code:** new `src/Dmon.Gateway` (ASP.NET Core, Kestrel WebSocket); depends on `Dmon.Runtime` (`CoreProcessManager`, ADR-011) and the `agent-profiles` resolver. No change to `Dmon.Core`'s stdio protocol.
- **Protocol:** new gateway-only connection-control frames; ADR-003 command/event shapes unchanged.
- **Storage:** consumes `messages.jsonl` (ADR-004) as the replay cursor (per-session `seq`); no log-format change.
- **Config:** gateway settings — bind address (loopback default), detached/idle/running TTLs, max concurrent handlers, optional shared key.
- **Dependencies:** **`agent-profiles`** change must land first (the `profile` selected at session creation). ASP.NET Core is a new dependency, isolated to `Dmon.Gateway`.
- **Deployment:** Tailscale on the home server (`tailscale serve` + MagicDNS cert); a tailnet ACL restricts the device to the gateway port.
- **ADRs:** implements ADR-012; relies on ADR-003/004/006/011 and the `agent-profiles` change (ADR-013). No ADR superseded.
- **Deferred (ADR-012 Open Questions):** read-only multi-observer (A), replay compaction (D) remain out of scope.
