## Why

The `agent-profiles` capability already mandates that a profile be selectable via "an optional per-session `profile` parameter at session creation" (spec: *Profile selection*, *Session-scoped single-sourced resolution*), but the wiring does not exist: `TurnHandler` resolves the profile with `requestedProfile: null` and carries a standing comment that per-session selection is "unwired in the RPC protocol". `SessionCreateCommand` has no `profile` field and `CoreLauncher` takes none. As a direct consequence, the `remote-session-gateway` change had to **defer** its profile-selecting session-creation group (§10): the gateway can resume existing sessions but cannot create one under a chosen profile, because making "the core runs under that profile" true requires changes across `Dmon.Protocol` + `Dmon.Core` that are out of that gateway-scoped change. This change closes that gap end-to-end.

## What Changes

- **Protocol (`Dmon.Protocol`):** add an optional `profile` field to the session-create surface (`SessionCreateCommand`). ADR-003 command shape gains one optional, backward-compatible property — omitting it preserves today's default-profile behaviour.
- **Core (`Dmon.Core`):** thread the requested `profile` from the create command through the dispatch/turn path into `AgentProfileContext.EnsureResolvedAsync` so the resolved `AgentProfile` (persona, asset flag, permission mode) genuinely governs the session — fulfilling the existing `agent-profiles` requirements. An unknown profile produces the hard, actionable error the resolver already raises (`AgentProfileConfigException`) at session start, with no partially-created session.
- **Gateway (`Dmon.Gateway`):** implement session creation: allocate a `sessionId`, resolve/validate the requested profile via the already-DI-wired `IAgentProfileResolver`, provision the per-session storage directory (ADR-004), spawn the core under that profile via `CoreLauncher`, and register the handler via `SessionRegistry.TryRegister` so the `MaxConcurrentHandlers` cap (already built in the gateway) is enforced — rejecting at cap. An unknown/unavailable profile fails creation with an actionable error and spawns no handler.
- **Spec hygiene:** relocate the "Profile-selecting session creation" requirement out of the in-flight `remote-session-gateway` change's spec delta into this change, so the gateway change's standing spec does not claim behaviour it deliberately deferred.

## Capabilities

### New Capabilities
- `profile-selecting-session-creation`: a client may create a session under a named agent profile; the per-session `profile` travels the ADR-003 create surface and is threaded into single-sourced profile resolution so the core runs under it; the gateway performs profile-validated, cap-bounded session creation with ADR-004 storage provisioning; unknown profiles fail with an actionable error and create nothing.

### Modified Capabilities
<!-- None at the requirement-text level. This change *implements* the existing
     `agent-profiles` requirements (Profile selection / Session-scoped single-sourced
     resolution), which already mandate the behaviour, without changing their wording. -->

## Impact

- **Code:** `Dmon.Protocol` (`SessionCreateCommand`), `Dmon.Core` (`CommandDispatcher` → session/`TurnHandler` → `AgentProfileContext.EnsureResolvedAsync`; possibly `CoreLauncher`/session bootstrap for spawn-time profile), `Dmon.Gateway` (a session-creation surface + `CoreLauncher` spawn + ADR-004 dir provisioning + `SessionRegistry.TryRegister`).
- **Protocol:** one optional, backward-compatible field on `session.create` (ADR-003). Existing clients that omit it are unaffected.
- **Specs/ADRs:** new `profile-selecting-session-creation` capability; honours ADR-001/003/004, ADR-013 (agent profiles), ADR-012 (gateway). Requires the companion edit to the `remote-session-gateway` change delta (remove the relocated requirement).
- **Depends on:** the archived `agent-profiles` change (resolver + `AgentProfile` types) and the merged `remote-session-gateway` gateway (attach-only flow + `TryRegister` cap primitive).
