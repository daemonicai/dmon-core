## 1. Protocol: profile on the session-create surface

- [ ] 1.1 Add an optional `profile` (`string?`, JSON `"profile"`) to `SessionCreateCommand` in `src/Dmon.Protocol/Commands/SessionCommands.cs`; keep it nullable/omittable so `session.create` stays wire-backward-compatible.
- [ ] 1.2 Add/extend `Dmon.Protocol.Tests` to assert round-trip (de)serialisation with and without `profile`, and that an absent field deserialises to null.

## 2. Core: thread requestedProfile into single-sourced resolution

- [ ] 2.1 Carry the create command's `profile` to the session's profile resolution: record it when `session.create` is handled (`Dmon.Core.Rpc.SessionHandler.CreateAsync` / the session context) and pass it as `requestedProfile` into `AgentProfileContext.EnsureResolvedAsync`, replacing the hard-coded `null` at `TurnHandler.cs:~116` and removing the "unwired" comment.
- [ ] 2.2 On an unknown/unavailable profile, surface the resolver's `AgentProfileConfigException` as an actionable `session.create` failure (error `ResponseEvent`), creating no usable session; confirm the resolved `AgentProfile` still feeds prompt builder + asset provisioning + permission gate exactly once per session (no regression to `agent-profiles` single-sourcing).
- [ ] 2.3 Verify omitting `profile` preserves today's `defaultProfile → coding` behaviour byte-for-byte (existing `Dmon.Core.Tests` stay green; add a per-session-profile test).

## 3. Gateway: profile-validated, cap-bounded session creation

- [ ] 3.1 Add a `create` connection-control frame (`gw:"create"`, carries `profile`) and a `created` reply (`sessionId`, `generation`, `headSeq`); define an actionable control-frame error for creation failures. Keep `attach` semantics unchanged (still requires an existing `sessionId`).
- [ ] 3.2 Implement the creation flow in the gateway: resolve/validate the profile via `IAgentProfileResolver` (fail fast, no spawn on unknown) → check/enforce the cap → provision via the core's `session.create {profile}` over a freshly spawned `CoreSession` (`CoreLauncher`, ADR-004 dir provisioned by the core) → register via `SessionRegistry.TryRegister` (enforcing `MaxConcurrentHandlers`) → reply `created`.
- [ ] 3.3 Enforce "no residue": on unknown profile, full cap, provisioning error, or spawn/init failure (incl. a lost `TryRegister` race), tear down any spawned core + storage and register nothing; return the actionable error.
- [ ] 3.4 Reattach to an existing session stays cap-free (uses `TryGet`/`Attach`, never `TryRegister`); confirm the unknown-session `attach` path (4404) is unchanged.

## 4. Tests

- [ ] 4.1 Core: `session.create {profile}` runs the session under that profile; unknown profile → actionable error, no usable session; omitted profile → default behaviour.
- [ ] 4.2 Gateway: create-under-profile returns a `sessionId` and a subsequent `attach` binds it; unknown profile → actionable error + no handler/process; creation at the cap → rejected + no spawn; partial-failure teardown leaves no registered handler/orphaned process.

## 5. Spec hygiene and docs

- [ ] 5.1 Remove the "Profile-selecting session creation" requirement from `openspec/changes/remote-session-gateway/specs/remote-session-gateway/spec.md` (relocated here); note the relocation in that change's DEVLOG. If `remote-session-gateway` has already archived, amend its standing spec instead.
- [ ] 5.2 Update `docs/deploying-the-gateway.md` to remove the "session creation is deferred" caveat and document creating a session under a profile over the gateway.
- [ ] 5.3 `openspec validate profile-selecting-session-creation --strict` clean; full build (0 warnings) + test suite green.
