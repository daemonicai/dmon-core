# dmon WebSocket Gateway — Client Protocol Guide

This guide is for engineers building a client (e.g. a Swift/iOS app) against the dmon
WebSocket gateway. It explains how to implement a correct client from this directory alone.
It does **not** duplicate normative behaviour — it cites the authoritative sources instead.

**Type source of truth:** [`schema.json`](./schema.json) in this directory. Generate or
validate your Swift `Codable` types against it. Every frame shape shown in this guide must
be valid against that schema.

**Normative sources:**
- `openspec/specs/remote-session-gateway/spec.md` — gateway behaviour (cited as **GW-REQ:
  \<name\>** below).
- [`docs/adrs/ADR-003`](../adrs/ADR-003-rpc-protocol.md) — command/event wire shape.
- [`docs/adrs/ADR-011`](../adrs/ADR-011-distribution-model.md) — protocol versioning.
- [`docs/adrs/ADR-012`](../adrs/ADR-012-remote-session-transport.md) — gateway transport
  design.
- [`docs/adrs/ADR-014`](../adrs/ADR-014-gateway-event-replay.md) — in-memory seq replay.
- [`docs/adrs/ADR-015`](../adrs/ADR-015-typed-result-events.md) — typed result events.
- [`docs/adrs/ADR-016`](../adrs/ADR-016-conversation-persistence.md) — conversation parts.

---

## Contents

1. [Protocol versioning](#1-protocol-versioning)
2. [Two channels on one socket](#2-two-channels-on-one-socket)
3. [Connection lifecycle](#3-connection-lifecycle)
4. [Reliability — seq, replay, and fencing](#4-reliability--seq-replay-and-fencing)
5. [Commands and results](#5-commands-and-results)
6. [Permission prompts](#6-permission-prompts)
7. [Authentication](#7-authentication)
8. [Worked example](#8-worked-example)

---

## 1. Protocol versioning

The schema carries a top-level `x-protocolVersion` field (currently `"0.2"`). This value
comes from `ProtocolVersion.Current` in the server and is stamped into the schema at build
time (ADR-011).

The version scheme is `Major.Minor[.Patch]` where **`Major.Minor` identifies the wire
contract**. Patch changes carry no wire changes and need no client action.

A client should check `x-protocolVersion` when generating code from the schema. If the
`Major.Minor` segment of the server's protocol version differs from the one the client was
generated against, the client and server are running incompatible wire contracts and the
connection should be refused with an informative error shown to the user.

There is no version-negotiation frame on the wire today; version compatibility is a
deploy-time check.

---

## 2. Two channels on one socket

All traffic flows over a single WebSocket. Inbound frames (server → client) fall into two
mutually exclusive routing paths, distinguished by a single field test:

| Condition | Channel | Route to |
|-----------|---------|----------|
| Frame JSON contains a top-level `"gw"` field | Control channel | Your control-frame handler |
| Frame JSON has no `"gw"` field | ADR-003 channel | Your command/event dispatcher |

**Control frames** (discriminated by `"gw"`) are the handshake and liveness layer defined
by ADR-012. They wrap ADR-003 traffic without touching it.

**ADR-003 frames** (discriminated by `"type"`) are the forwarded core events and
command-result events. The gateway passes them byte-for-byte from the core process; their
shapes are defined by ADR-003 and ADR-015.

Control frames are additive and will never reshape or embed ADR-003 messages. The two
channels are always distinguishable by this single field test (GW-REQ: Connection-control
sub-protocol).

The `$defs` in `schema.json` mirror this split: `command` and `event` hold the ADR-003
family; `gw.attach`, `gw.attached`, `gw.ack`, `gw.create`, `gw.created`,
`gw.createRejected`, `gw.ping`, and `gw.pong` hold the control family.

---

## 3. Connection lifecycle

### 3.1 WebSocket upgrade

Open a standard WebSocket connection to the gateway's `/ws` endpoint. See
[Section 7](#7-authentication) for the `Authorization` header required when a shared key
is configured.

### 3.2 Starting a new session — `create`

If you do not yet have a `sessionId`, send a `create` frame as the very first frame:

```json
{"gw":"create","profile":"coding"}
```

`profile` is optional. Omit it (or set it to `null`) to use the server default. An unknown
profile name returns `createRejected` without spawning a process.

Possible responses:

```json
{"gw":"created","sessionId":"s-abc123"}
```

```json
{"gw":"createRejected","code":"unknown_profile","message":"Profile 'foo' is not defined"}
```

```json
{"gw":"createRejected","code":"cap_reached","message":"Session cap reached"}
```

On `created`, proceed to `attach` using the returned `sessionId`.

### 3.3 Attaching to a session — `attach`

```json
{"gw":"attach","sessionId":"s-abc123","lastSeq":0}
```

Set `lastSeq` to the highest sequence number you have already processed (use `0` on a
fresh attach). The gateway uses this to replay any events you missed (see
[Section 4](#4-reliability--seq-replay-and-fencing)).

The gateway replies:

```json
{"gw":"attached","generation":1,"headSeq":0}
```

`generation` is a monotonically increasing counter for this session (explained in
[Section 4.3](#43-generation-fencing)). `headSeq` is the highest sequence number assigned
to any server→client event so far. On a brand-new session this is `0`.

After `attached`, the connection enters the live phase. Events from the core arrive as
ADR-003 frames; commands you send are forwarded to the core and acknowledged with `ack`.

### 3.4 First-frame rules

The gateway accepts `attach` or `create` as the first frame on a connection. Any other
first frame closes the connection.

---

## 4. Reliability — seq, replay, and fencing

### 4.1 Per-session sequence numbers

The gateway assigns a strictly increasing, gapless, per-session sequence number (`seq`) to
every ADR-003 event frame as it flows out of the core process. Seq starts at 1. The
sequence is tracked in the gateway's in-memory buffer (ADR-014; it is **not** stored in
`messages.jsonl`, which holds only conversational turns).

**`seq` is never written into the JSON body of an ADR-003 frame.** The gateway forwards
core event frames byte-unchanged; no `seq` field appears on the wire. A client cannot read
a seq off an event. The only seq value a client ever receives from the server is `headSeq`
in each `attached` frame.

`lastSeq` is a **client-authored cursor** — it is derived and tracked entirely by the
client (see §4.2 below) and sent to the gateway on each `attach`.

GW-REQ: Per-session event sequencing — each event is assigned a strictly increasing `seq`
with no gaps or reuse within the session.

### 4.2 Replay on reattach

When you reattach after a disconnect, send your current `lastSeq` cursor. The gateway
delivers every event with a higher `seq` up to `headSeq` before resuming live delivery.
Subscribe-then-replay ordering is guaranteed: no event arriving during replay is dropped
or duplicated (ADR-014).

GW-REQ: Replay on reattach.

**How to compute `lastSeq` (the only correct method):**

Because `seq` is never stamped into event frames on the wire (§4.1), a client derives it
by counting:

1. On `attached`, record `headSeq` as your cursor baseline. For a brand-new session
   `headSeq` is 0, meaning no events have been emitted yet; the first event you receive
   will be seq 1.
2. For every ADR-003 event frame you receive after `attached`, increment your cursor by
   one. Control frames (`ack`, `ping`, `pong`) do **not** advance the cursor.
3. Your cursor's current value is the `lastSeq` to send on your next `attach`.

The replay seam is always `[lastSeq+1 … headSeq]` replayed in order, then live from
`headSeq+1`. Ordering is guaranteed by delivery order, not by an in-band field.

GW-REQ: Replay on reattach — Scenario: No duplicate across the replay seam.

### 4.3 Generation fencing

Each `attached` response carries a `generation` counter. Every `attach` to a handler
increments the generation. If a second client attaches to the same session while your
connection is live, the gateway closes your connection — you have been fenced.

GW-REQ: Stale-connection fencing and single active writer.

Practical consequence: when your connection drops unexpectedly, reattach promptly. If you
receive a close before you expect it, treat it as an eviction and decide whether to
reattach or surface an error to the user.

### 4.4 Heartbeat

The gateway runs application-level `ping`/`pong` on a server-configured interval to
survive carrier NAT idle timeouts. Either side may send `ping`:

```json
{"gw":"ping"}
```

The receiving side replies immediately with:

```json
{"gw":"pong"}
```

GW-REQ: Heartbeat liveness — a dead connection is detected and the detached-lifetime
grace timer begins on *detected* disconnect (not on socket close).

---

## 5. Commands and results

### 5.1 Sending a command

Commands flow client → gateway → core. Each command must carry a unique `id`:

```json
{"type":"turn.submit","id":"cmd-1","message":"Explain monads"}
```

The gateway sends `ack` immediately on receipt (before the core has processed it):

```json
{"gw":"ack","id":"cmd-1"}
```

### 5.2 Receiving a result

When the core finishes processing a command, it emits a typed result event. Every result
event carries an `id` field that echoes the originating command `id` (ADR-015):

```json
{"type":"session.createResult","id":"cmd-0","session":{"id":"s-abc123"}}
```

There is **no** generic `{"type":"response",...}` envelope. Failures are:

```json
{"type":"commandError","id":"cmd-1","command":"turn.submit","code":"noActiveSession","message":"..."}
```

Correlate responses to commands using the `id` field. This is especially important on
reconnect: you may resend a command that was already delivered before the disconnect. The
gateway deduplicates commands by `id` within a session, so a resent command that was
already forwarded to the core is silently dropped (GW-REQ: Command idempotency across
reconnects).

### 5.3 Streaming events

`turn.submit` produces a stream of notification events (none of which are result events and
none carry a command `id`):

| `type` | Meaning |
|--------|---------|
| `turnStart` | The agentic loop began |
| `messageStart` | The model started emitting a message |
| `messageDelta` | Incremental content chunk |
| `messageEnd` | The model finished this message |
| `toolExecutionStart` | A tool call began |
| `toolExecutionEnd` | A tool call finished |
| `turnEnd` | The agentic loop finished; carries the final `message` and `toolResults` |

The turn sequence is `turnStart` → (one or more message/tool cycles) → `turnEnd`. Render
deltas incrementally from `messageDelta` events; `messageEnd` carries the final assembled
message.

---

## 6. Permission prompts

While a turn is running, the core may require user confirmation before executing a tool.
The gateway holds the turn at the permission gate and delivers a `tool.confirmRequest`
event:

```json
{
  "type": "tool.confirmRequest",
  "id": "<request-id>",
  "name": "<tool-name>",
  "args": {...},
  "risk": "Medium"
}
```

Required fields: `id`, `name`, `args`. `risk` is optional; values are `None | Low | Medium | High`.

Respond with a `tool.confirmResponse` command, echoing the same `id`. Only `id` is
required; `confirmed`, `cancelled`, and `scope` are all optional:

```json
{
  "type": "tool.confirmResponse",
  "id": "<request-id>",
  "confirmed": true,
  "scope": "once"
}
```

`ui.inputRequest` asks for a value from the user:

```json
{
  "type": "ui.inputRequest",
  "id": "<request-id>",
  "prompt": "Enter your API key",
  "kind": "Secret"
}
```

Required fields: `id`, `prompt`. `kind` is optional; values are `Text | Secret | Select`
(default `Text`). When `kind` is `Secret`, mask the input field. When `kind` is `Select`,
the `options` array carries the choices. Respond with `ui.inputResponse` (only `id` is
required; also accepts `value` and `cancelled`).

**Parking while detached:** if the connection drops while the turn is waiting at a
permission gate, the gateway parks the request. On reattach the parked request is always
re-emitted with a fresh `seq` above `headSeq`, so it arrives as a live-ordered frame
after the replay window closes — never inside the replay window. Do not auto-approve or
auto-deny a parked prompt; present it to the user for resolution.

GW-REQ: Permission prompts park while detached.

---

## 7. Authentication

The gateway is designed for single-tenant, private-network use (ADR-012). Transport
encryption and device identity are provided by Tailscale (`tailscale serve` fronts the
loopback gateway with a Let's Encrypt certificate for the MagicDNS hostname). The gateway
binds loopback by default and does not listen on public interfaces.

**Optional shared key:** when the server is configured with a pre-shared key, include it on
the WebSocket upgrade request:

```
Authorization: Bearer <your-key>
```

A missing or mismatched key returns HTTP 401 before the WebSocket handshake completes. No
socket is opened on a 401 (GW-REQ: Tailscale-fronted authentication — Scenario: Shared key
enforced on upgrade).

---

## 8. Worked example

This section shows literal JSON frames for two scenarios required by the protocol spec:

1. Connect → attach → submit a turn → receive streamed events through `turnEnd`
2. Drop the connection mid-session, then reconnect with `lastSeq` and receive replayed
   events

All frames are valid against `schema.json`. Frames are shown in delivery order with the
direction noted.

**Note on `message` and `delta` payloads:** in `schema.json` the `message` field on
`messageStart`/`messageDelta`/`messageEnd` and the `delta` field on `messageDelta` are
modelled as **opaque** (`true`). The gateway forwards the core's objects unchanged; their
internal shape is render-only and **not** part of the wire contract. Do not hard-code a
fixed parse shape; treat them as opaque JSON. The illustrative shapes below are
non-normative. Real `delta` objects carry a `type` discriminator whose values include
`start`, `textStart`, `textDelta`, `textEnd`, `thinkingStart`, `thinkingEnd`, and others
defined in `src/Dmon.Protocol/Delta/MessageDelta.cs`.

### Scenario A — Happy-path turn

```
// 1. Client opens WebSocket to wss://<hostname>/ws
//    (with Authorization: Bearer <key> if a shared key is configured)

// 2. Client → gateway: create a new session (or skip to step 4 if session exists)
→ {"gw":"create","profile":"coding"}

// 3. Gateway → client: session created
← {"gw":"created","sessionId":"s-d3f1a2b4"}

// 4. Client → gateway: attach to the session (lastSeq:0 = fresh attach)
→ {"gw":"attach","sessionId":"s-d3f1a2b4","lastSeq":0}

// 5. Gateway → client: attach accepted
//    generation:1 = first attach to this session
//    headSeq:0 = no events yet; the first event will be seq 1
//    Client sets its lastSeq cursor to 0.
← {"gw":"attached","generation":1,"headSeq":0}

// 6. Client → gateway: submit a turn
→ {"type":"turn.submit","id":"cmd-1","message":"What is 2 + 2?"}

// 7. Gateway → client: command acknowledged
← {"gw":"ack","id":"cmd-1"}
//    ack is a control frame — does NOT advance the lastSeq cursor.

// 8. Gateway → client: agentic loop started
//    (forwarded unchanged from core; no "gw" field → ADR-003 event channel)
//    Client increments cursor: lastSeq = 1
← {"type":"turnStart"}

// 9. Gateway → client: model began emitting a message
//    Client increments cursor: lastSeq = 2
← {"type":"messageStart","message":<opaque>}

// 10. Gateway → client: incremental content delta
//     Client increments cursor: lastSeq = 3
//     delta shape is opaque (non-normative illustration: {"type":"textDelta","text":"4"})
← {"type":"messageDelta","message":<opaque>,"delta":<opaque>}

// 11. Gateway → client: model finished this message
//     Client increments cursor: lastSeq = 4
← {"type":"messageEnd","message":<opaque>}

// 12. Gateway → client: agentic loop finished
//     Client increments cursor: lastSeq = 5
← {"type":"turnEnd","message":<opaque>,"toolResults":[]}

// Session is now idle. headSeq is 5 (events 8–12 got seq 1–5). Client's cursor = 5.
```

### Scenario B — Reconnect with replay

Continuing from Scenario A: the connection drops after `messageStart` (seq 2) is delivered
but before `messageDelta`, `messageEnd`, and `turnEnd` are received.

```
// State at disconnect:
//   headSeq = 5  (gateway has assigned seq 1–5 to the five events above)
//   Client's lastSeq cursor = 2  (cursor was incremented for turnStart + messageStart)

// 1. Client re-opens the WebSocket

// 2. Client → gateway: reattach, sending the cursor value as lastSeq
→ {"gw":"attach","sessionId":"s-d3f1a2b4","lastSeq":2}

// 3. Gateway → client: attach accepted
//    generation:2 = second attach to this session (generation incremented)
//    headSeq:5 = five events have been assigned seqs in this session
//    Client resets its cursor baseline to headSeq (5); replay will bring it to 5.
← {"gw":"attached","generation":2,"headSeq":5}

// 4. Gateway replays events with seq > lastSeq (seq 3, 4, 5) in order.
//    Replayed frames are byte-identical to the originals; no seq field is added.
//    Client increments cursor on each: 2 → 3 → 4 → 5
← {"type":"messageDelta","message":<opaque>,"delta":<opaque>}
← {"type":"messageEnd","message":<opaque>}
← {"type":"turnEnd","message":<opaque>,"toolResults":[]}

// 5. Replay complete (cursor = headSeq = 5). Live delivery resumes from seq 6 onwards.
//    Any new event emitted by the core will appear here as a fresh ADR-003 frame.
```

**Key points illustrated:**
- The client derived `lastSeq:2` by counting two ADR-003 event frames received since
  `attached`; it did not read a `seq` field off any frame.
- `generation` increased from 1 to 2, confirming a new attach cycle.
- `headSeq:5` in `attached` tells the client how many replayed events to expect (5 − 2 = 3)
  before live delivery resumes. The client can use this to know when the replay seam ends.
- The replayed events are byte-identical to what was originally sent; no fields are added
  or modified by the replay path.
- After the seam, live events begin at seq 6. The client should deduplicate by tracking its
  own `lastSeq` cursor and ignoring any event at or below it.

---

## Appendix: `gw` discriminator values

All eight control-frame `gw` values, their direction, and their `$defs` key in
`schema.json`:

| `gw` value | Direction | `schema.json` `$defs` key |
|------------|-----------|--------------------------|
| `attach` | client → gateway | `gw.attach` |
| `attached` | gateway → client | `gw.attached` |
| `ack` | gateway → client | `gw.ack` |
| `create` | client → gateway | `gw.create` |
| `created` | gateway → client | `gw.created` |
| `createRejected` | gateway → client | `gw.createRejected` |
| `ping` | either direction | `gw.ping` |
| `pong` | either direction | `gw.pong` |
