## Why

The `agent-profiles` spec already mandates per-session profile selection — an optional `profile` at session creation, immutable for the session's life, with a hard error on unknown names. But that selection is not yet *wired*: `TurnHandler` resolves with a hardcoded `requestedProfile: null` (`src/Dmon.Core/Rpc/TurnHandler.cs:121`), nothing carries a profile name from session creation, nothing persists it, and the gateway has no way to create a session at all (its WebSocket surface is attach-only). This change makes the existing contract true and completes the two deferred `remote-session-gateway` tasks (10.1/10.2).

## What Changes

- Persist the selected profile name in the session record: `SessionMeta` gains a `profile` field serialised to `meta.json` (ADR-004). The persisted record becomes the single source the core's resolver reads — no new in-memory plumbing through `TurnHandler`.
- Carry the profile through core session creation: `SessionCreateCommand` gains an optional `profile`; `SessionStore.CreateAsync` writes it into the new session's `meta.json`. `TurnHandler` resolves with `_sessionHandler.CurrentSession?.Profile` instead of `null`. The resolver and `AgentProfileContext` are unchanged — they already accept a `requestedProfile`.
- A reloaded session keeps its profile: because the name lives in `meta.json`, `session.load` rehydrates it for free and resolution at the next turn is unchanged.
- Fork and clone **inherit** the parent's profile: `SessionStore.ForkAsync`/`CloneAsync` copy `Profile` into the new `meta.json`. No new command fields (inherit-only for V1).
- The gateway gains a **`create` control frame** (sibling to the existing `attach` frame). On create the gateway validates the requested profile against its already-wired `IAgentProfileResolver` **before** spawning anything: an unknown profile is rejected with no core spawned and no registry entry (deferred task 10.2); a known profile spawns a core via `CoreLauncher`, drives it through `session.create{profile}`, registers the handler under the `MaxConcurrentHandlers` cap, and returns the new `sessionId` for the client to attach to (deferred task 10.1).
- Completes `remote-session-gateway` tasks **10.1** and **10.2**.

## Capabilities

### New Capabilities

_None._ This change wires and persists behaviour that existing capabilities already specify, and completes a requirement deferred by `remote-session-gateway`.

### Modified Capabilities

- `session-storage`: `meta.json` carries a `profile` field; session fork and clone copy the parent's profile into the new session record.
- `agent-profiles`: the per-session `profile` is sourced from the persisted session record (`meta.json`) and survives session reload; the resolver input is wired from this persisted name rather than a hardcoded null.
- `remote-session-gateway`: the WebSocket surface gains a `create` control frame; the gateway resolves and validates the requested profile **before** spawning a core, rejecting unknown profiles without spawning a handler, and registers the spawned handler under the concurrent-handler cap. (This realises the capability's existing "Profile-selecting session creation" requirement; **sequencing**: `remote-session-gateway` must be archived first so its spec stands in `openspec/specs/` for this delta to target.)

## Impact

- **Protocol** (`Dmon.Protocol`): `SessionMeta` and `SessionCreateCommand` gain a `profile` field (dmon-owned types only, ADR-016). New gateway `create`/`created` control frames (typed, correlated — ADR-015), not a generic envelope.
- **Core** (`Dmon.Core`): `SessionStore.CreateAsync`/`ForkAsync`/`CloneAsync` persist/inherit `profile`; `TurnHandler` reads the persisted profile for resolution. No constructor or DI changes — `AgentProfileResolver` and `AgentProfileContext` are untouched.
- **Gateway** (`Dmon.Gateway`): new create flow in `GatewayConnectionEndpoint`, new control frames in `ControlFrames`/`ControlFrameSerializer`, profile validation via the existing resolver wiring (`Program.cs`), cap enforcement via `SessionRegistry.TryRegister` (the §7 primitive).
- **ADRs honoured**: ADR-004 (profile persists in `meta.json`), ADR-012 (single-tenant Tailscale gateway — pre-spawn validation is an early-rejection convenience; the core's first-turn resolution stays authoritative), ADR-013 (this is the "selected per session" realisation), ADR-015 (typed create result/error events), ADR-016 (no third-party types on the wire).
- **Closes** deferred `remote-session-gateway` tasks 10.1 and 10.2.
- **Sequencing dependency**: archive `remote-session-gateway` before applying this change so its standing spec exists to be modified.
