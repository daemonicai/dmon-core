## Context

Per-session profile selection is specified by `agent-profiles` (*Profile selection*, *Session-scoped single-sourced resolution*) but unimplemented: `TurnHandler` calls `AgentProfileContext.EnsureResolvedAsync(resolver, requestedProfile: null, …)` with a standing comment that the RPC path is unwired, `SessionCreateCommand` has no `profile` field, and `CoreLauncher.StartProtocolCompatibleCoreAsync` takes none. The `remote-session-gateway` change therefore deferred its §10 (profile-selecting session creation): the gateway today only re-attaches to an existing handler (`GatewayConnectionEndpoint` closes unknown sessions with 4404) and has no creation surface; its `SessionRegistry.TryRegister`/`MaxConcurrentHandlers` cap primitive exists but has no production caller.

Existing machinery to build on (verified):
- **Core:** `SessionCreateCommand` → `CommandDispatcher` → `Dmon.Core.Rpc.SessionHandler.CreateAsync` → `ISessionStore.CreateAsync` (allocates the `Guid` id, provisions `<root>/<id>/{messages.jsonl,attachments/,meta.json}` per ADR-004, emits a `ResponseEvent{Command:"session.create", Data: SessionMeta}`). The core allocates the id.
- **Profiles:** `IAgentProfileResolver.ResolveAsync(requestedProfile, ct)` → `AgentProfile`; unknown name → `AgentProfileConfigException` (hard, actionable). `AgentProfileContext.EnsureResolvedAsync(resolver, requestedProfile, ct)` resolves once and caches. Already DI-wired in the gateway's `Program.cs`.
- **Runtime:** `CoreLauncher.StartProtocolCompatibleCoreAsync(corePathOverride, workingDirectory, onStderrLine, ct)` → `CoreSession`; the gateway `SessionHandler` wraps a `CoreSession`.

## Goals / Non-Goals

**Goals:**
- A per-session `profile` carried on the ADR-003 create surface and threaded into single-sourced resolution so the core genuinely runs under it.
- A gateway session-creation flow: validate profile → provision → spawn-under-profile → cap-bounded register → return the new `sessionId`; unknown profile or full cap fails cleanly with no residue.
- Relocate the deferred "Profile-selecting session creation" requirement from the `remote-session-gateway` change delta into this change.

**Non-Goals:**
- Changing profile resolution *semantics* (precedence, asset provisioning, permission mode) — those stay as `agent-profiles` defines; this change only *feeds* `requestedProfile`.
- Multi-core orchestration, session migration, or any V1-out-of-scope item.
- The iOS client UI for choosing a profile (client concern).

## Decisions

**D1 — The gateway creates a session by spawning a fresh core and sending `session.create {profile}` over ADR-003 stdio, exposed to the client as a new `create` control frame.** The gateway spawns one core per session (it already owns one `CoreSession` per handler), so the per-session profile rides the existing `session.create` command rather than a new core-spawn argument. Client-facing, creation is a new connection-control frame (`gw:"create"`, carries `profile`; gateway replies `created {sessionId, generation, headSeq}`), kept distinct from `attach` (which still requires an existing `sessionId`). *Alternatives: a separate gateway HTTP route — rejected, it forks a second control plane off the ADR-003 pipe; overloading the first `attach` to also create — rejected, conflates resume with create and muddies the generation/replay contract.*

**D2 — `profile` is an optional field on `SessionCreateCommand`, threaded core-side into `AgentProfileContext.EnsureResolvedAsync`.** On `session.create`, the core records the `requestedProfile` for the session and the (lazy, first-turn) `EnsureResolvedAsync` consumes it instead of `null`. Null/absent preserves today's `defaultProfile → coding` fallback, so `Dmon.Terminal` and existing clients are wire-unchanged. *Alternative — a new dedicated command — rejected: `session.create` is exactly the lifecycle point `agent-profiles` names ("at session creation").*

**D3 — Fail fast, in order, with no residue: resolve → cap → provision → spawn → register; tear down on any later failure.** The gateway resolves/validates the profile via `IAgentProfileResolver` **before** spawning anything (cheap, and an unknown profile must spawn no core). It checks the cap before spawning; the final `TryRegister` is the authoritative gate (the §7 benign TOCTOU stands). Any failure after a core is spawned (cap lost to a race, init error) tears the core + storage down so nothing half-created is left registered. *Alternative — spawn-then-validate — rejected: wastes a process and risks orphans on the common unknown-profile path.*

**D4 — Validate in both gateway and core (defense in depth), against the same effective profile set.** The gateway validates to fail fast and avoid a doomed spawn; the core re-validates on `session.create` so non-gateway hosts (Terminal) get the same hard error. Both must resolve against the **same** effective set — the gateway spawns the core in the working directory it resolved against, so user+project `config.yaml` agree. *This double-check is intentional, not redundant — the core is the source of truth; the gateway check is an optimisation + better error surface.*

**D5 — Relocate, don't duplicate, the gateway requirement.** The "Profile-selecting session creation" requirement is removed from the `remote-session-gateway` change's spec delta and owned here, so when the gateway change archives its standing spec does not claim deferred behaviour.

## Risks / Trade-offs

- **[Effective profile set diverges between gateway and core]** → Gateway spawns the core in the same working directory it resolved against; the core re-validates and is the source of truth. A mismatch surfaces as the core's actionable error, not a silent wrong-profile run.
- **[Cap TOCTOU: two concurrent creates both pass the pre-check then one `TryRegister` fails]** → The losing create tears its just-spawned core down and returns the actionable cap error. Window is small; single-tenant home-server. Same benign race already accepted in §7.
- **[Orphaned core/storage on partial failure]** → D3 mandates teardown on any post-spawn failure; covered by the "Failed creation leaves no residue" requirement and a test.
- **[`profile` field breaks older cores]** → Optional, additive field; absent ⇒ identical behaviour. ADR-003 JSON stays backward-compatible.
- **[Spawn-time vs first-turn resolution race]** → Profile is recorded at `session.create` (before any turn) and read at the single lazy `EnsureResolvedAsync`; resolution still happens exactly once per session, preserving the `agent-profiles` single-source guarantee.

## Migration Plan

1. Land the optional `profile` on `SessionCreateCommand` + core threading first (independently testable; no behaviour change when omitted).
2. Add the gateway `create` control frame + creation flow + cap enforcement + teardown.
3. Edit the in-flight `remote-session-gateway` change: remove the relocated requirement from its spec delta (note in its DEVLOG). If that change has already archived, instead amend its standing spec.
4. No rollback concern: the field is additive; reverting the gateway frame leaves the core change inert (clients simply omit `profile`).

## Open Questions

- **Exact `created` reply shape and error frame.** Should creation failures (unknown profile / cap) be a `gw:"error"` control frame with a code, or an HTTP status on a pre-upgrade route? Leaning control-frame to stay on the WS pipe (D1); confirm during apply against how the client is expected to consume it.
- **Does the core need a spawn-time fast-fail for an unknown profile** (e.g. exit non-zero) in addition to the `session.create` error response, or is the response-event error sufficient for the gateway to surface? Resolve when wiring D4.
