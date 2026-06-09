## Context

The `agent-profiles` capability already specifies per-session profile selection: an optional `profile` parameter at session creation, precedence over `defaultProfile`, immutability for the session's life, and a hard error on an unknown name. The resolution machinery exists and is complete — `IAgentProfileResolver.ResolveAsync(string? requestedProfile, …)` implements the precedence and unknown-profile handling, and `AgentProfileContext.EnsureResolvedAsync(resolver, requestedProfile, …)` passes the name straight through.

What is missing is the wiring and persistence:

- `TurnHandler` resolves the profile once per session (lazily, at first turn, behind the `_systemPromptInjected` guard) but passes a hardcoded `requestedProfile: null` (`src/Dmon.Core/Rpc/TurnHandler.cs:121`).
- `SessionCreateCommand` is an empty record — nothing carries a profile name into the core.
- `SessionMeta`/`meta.json` does not store a profile, so even if one were selected it would not survive reload.
- The gateway's WebSocket surface is **attach-only**: `GatewayConnectionEndpoint` requires the first frame to be `attach`, and an unknown session id closes the socket with a comment that creation "is Group 10". There is no `create` frame.

This is the follow-up split out of `remote-session-gateway` (user decision, 2026-05-31), which left tasks 10.1/10.2 deferred precisely because making the gateway spawn a profile-bound core requires changes spanning `Dmon.Protocol` and `Dmon.Core`, outside that gateway-only change's scope.

## Goals / Non-Goals

**Goals:**

- Wire the existing per-session profile selection end-to-end on the Terminal RPC path and persist it in the session record.
- Give the gateway a session-create surface that selects a profile, validating it before spawning any core.
- Make fork and clone carry the parent's profile.
- Complete `remote-session-gateway` tasks 10.1 and 10.2.

**Non-Goals:**

- Changing the resolver or its precedence rules (`AgentProfileResolver`, `AgentProfileContext` are untouched).
- A re-profile / change-profile-mid-session operation (the spec already mandates immutability).
- A profile override on fork/clone (inherit-only for V1).
- Any change to permission modes, persona assembly, or asset provisioning — those already consume the resolved `AgentProfile`.
- Multi-tenant or untrusted remote access (out of scope per ADR-012; this stays single-tenant Tailscale-fronted).

## Decisions

### D1 — The persistence record is the plumbing

Add `profile?` to `SessionMeta`, serialised to `meta.json`. `SessionStore.CreateAsync` writes it; `session.load` rehydrates it; `TurnHandler` resolves with `_sessionHandler.CurrentSession?.Profile`.

Because `CreateAsync` does not set the in-memory current session (only `LoadAsync` does — `SessionHandler.cs:153`), the real lifecycle is **create → load → first turn**, and `TurnHandler` already reads `_sessionHandler.CurrentSession`. So feeding it a real profile requires **no new constructor parameter, no new DI registration, and no string threaded through the orchestration layers** — the loaded `SessionMeta` carries the value to exactly the point that consumes it.

_Alternative considered:_ thread `requestedProfile` as an in-memory field from the create command through `SessionHandler` into `TurnHandler`. Rejected — it duplicates state that must be persisted anyway, and creates a second source of truth that can disagree with `meta.json` after a reload.

### D2 — Gateway gains a `create` control frame; validation happens pre-spawn

Introduce a `create {profile?}` control frame as a sibling to `attach`. On receipt the gateway:

1. Resolves/validates the requested profile against its already-wired `IAgentProfileResolver` (`Program.cs:44`). **Unknown ⇒ reject frame, no core spawned, no `TryRegister`** — satisfies 10.2's "spawn no handler".
2. Known ⇒ spawn a core via `CoreLauncher`, drive it through `session.create{profile}` then `session.load`, and `TryRegister` the handler under `MaxConcurrentHandlers` (the §7 cap primitive). Cap reached ⇒ reject, tear down the just-spawned core.
3. Return a `created {sessionId}` frame; the client then sends `attach {sessionId, lastSeq:0}` through the existing flow.

The create/created/reject frames are typed and correlated (ADR-015), not a generic `{type:"response"}` envelope.

_Alternative considered (validate in core only, gateway forwards blindly):_ rejected — it contradicts 10.2 ("no handler spawned") and wastes a core spawn + teardown on every typo.

_Alternative considered (no create frame; sessions created out-of-band):_ rejected by the user — the gateway is the remote client's only surface, so it must be able to create.

### D3 — Two resolvers, core is authoritative

The gateway resolves the profile to **reject early**; the core resolves at first turn to **apply**. They read the same user/project `config.yaml` set, so they normally agree. They can disagree only if the effective profile set changes between the gateway's validation and the core's first-turn resolution. For a single-tenant home server (ADR-012) this window is benign. The spec records that **the gateway's check is an early-rejection convenience and the core's resolution is authoritative** — the gateway does not pass a resolved `AgentProfile` object to the core (which would also break the core's standalone Terminal path).

### D4 — Profile is immutable from creation

The selected name is written at create and never mutated; there is no re-profile command. This matches the existing `agent-profiles` immutability requirement and removes any "mutable until first turn" ambiguity from the lazy resolution — nothing exposes a setter, and the persisted value is fixed.

### D5 — Fork and clone inherit the parent profile

`SessionStore.ForkAsync`/`CloneAsync` already copy the source directory and rewrite `meta.json` with new lineage (`parentSession`, `forkEntryId`). They additionally carry the source's `profile` into the new `meta.json`. No new fields on `SessionForkCommand`/`SessionCloneCommand`. A fork/clone therefore reproduces the parent's behaviour, consistent with copying its conversation.

## Risks / Trade-offs

- **Gateway and core profile-set divergence** → Mitigated by D3: single-tenant deployment, core is authoritative, gateway check is convenience-only. Documented in the spec.
- **`SessionCreateCommand` gains a field — wire-format change** → No production deployments exist yet, so no back-compat shim is needed (prefer a clean break); `profile` is optional, so existing Terminal callers that omit it keep today's fallback-to-`coding` behaviour.
- **Cap race on concurrent creates** → Already acknowledged in `SessionRegistry.TryRegister` as a benign TOCTOU acceptable for a personal server; this change does not worsen it.
- **Spawned-core teardown on cap-reached / core-spawn failure** → The create flow must dispose the `CoreSession` it spawned before it registers, so a rejected create leaks no process. Covered by tasks/tests.

## Migration Plan

1. **Prerequisite:** archive `remote-session-gateway` so its spec stands in `openspec/specs/remote-session-gateway/` for this change's delta to modify. Its deferred tasks 10.1/10.2 are explicitly carried into this change.
2. Implement core (D1, D5) and protocol additions; Terminal path works first and is testable in isolation.
3. Implement the gateway create frame (D2, D3).
4. No data migration: existing `meta.json` files without a `profile` field deserialise to `null`, which resolves to `defaultProfile`/built-in `coding` — identical to today.

## Open Questions

_None blocking._ All four design decisions (D1–D5) are settled. The only sequencing item is the archive prerequisite in the migration plan, which is a workflow step, not an open design question.
