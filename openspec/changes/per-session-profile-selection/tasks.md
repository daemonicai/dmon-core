## 1. Protocol — profile on the wire

- [x] 1.1 Add an optional `profile` (`string?`, `[JsonPropertyName("profile")]`) to `SessionCreateCommand` in `src/Dmon.Protocol/Commands/SessionCommands.cs`. No field added to `SessionForkCommand`/`SessionCloneCommand` (inherit-only — design D5).
- [x] 1.2 Add an optional `profile` (`string?`, `[JsonPropertyName("profile")]`) to `SessionMeta` in `src/Dmon.Protocol/Sessions/SessionMeta.cs`. Confirm it serialises to / deserialises from `meta.json` and that an absent field maps to null (no migration — design "Migration Plan" step 4).
- [x] 1.3 Define the gateway `create` / `created` control frames (and the create-error frame) in `src/Dmon.Gateway/Protocol/ControlFrames.cs` as typed records (ADR-015): `create {profile?}` (client→gateway), `created {sessionId}` (gateway→client), and a typed rejection carrying an actionable message. Keep them dmon-owned types (ADR-016).

## 2. Core — persist and inherit the profile

- [x] 2.1 Thread `profile` through `SessionStore.CreateAsync` (`src/Dmon.Core/Session/SessionStore.cs`) and `ISessionStore`: accept the optional profile and write it into the new session's `meta.json`. Update `SessionHandler.CreateAsync` (`src/Dmon.Core/Rpc/SessionHandler.cs`) to pass `cmd.Profile`.
- [x] 2.2 Make `SessionStore.ForkAsync` and `CloneAsync` copy the source session's `profile` into the new `meta.json` (design D5). No new command fields.
- [x] 2.3 In `src/Dmon.Core/Rpc/TurnHandler.cs` (line ~121), replace `requestedProfile: null` with `requestedProfile: _sessionHandler.CurrentSession?.Profile`. Do not change `AgentProfileResolver` or `AgentProfileContext`. Update the stale "null until the RPC protocol wires per-session profile selection" comment.

## 3. Core — tests

- [x] 3.1 Test that `SessionStore.CreateAsync` with a profile persists it to `meta.json`, and that creating without one leaves it null.
- [x] 3.2 Test that `LoadAsync` rehydrates the persisted `profile`, and that `TurnHandler` resolves with the persisted profile (and with `null` when none is stored) — assert the resolver receives the expected `requestedProfile`.
- [x] 3.3 Test that fork and clone copy the source profile into the new session record, including the no-profile case.

## 4. Gateway — profile-selecting session creation (completes remote-session-gateway 10.1)

- [x] 4.1 Parse the `create` frame in `src/Dmon.Gateway/Protocol/ControlFrameSerializer.cs` and accept it as a valid first frame alongside `attach` in `src/Dmon.Gateway/GatewayConnectionEndpoint.cs`.
- [x] 4.2 On `create`: spawn a core via `CoreLauncher`, drive it through `session.create {profile}` then `session.load`, construct the `SessionHandler`, register it with `SessionRegistry.TryRegister` under `MaxConcurrentHandlers`, and reply `created {sessionId}`. (Implements remote-session-gateway task 10.1.)
- [x] 4.3 On a cap-reached `TryRegister` failure, tear down the just-spawned `CoreSession` (no orphaned process), register nothing, and reply with a typed, actionable cap error.

## 5. Gateway — pre-spawn profile validation (completes remote-session-gateway 10.2)

- [x] 5.1 Before spawning any core, validate the requested profile against the effective set via the gateway's wired `IAgentProfileResolver` (`Program.cs`). On an unknown profile, reply with a typed, actionable error naming the profile and spawn no core / register no handler. (Implements remote-session-gateway task 10.2.)
- [x] 5.2 Record in code/comment that gateway validation is an early-rejection convenience and the core's first-turn resolution is authoritative (design D3); the gateway forwards the profile *name*, not a resolved `AgentProfile`.

## 6. Gateway — tests

- [ ] 6.1 Test create-with-known-profile: a core is spawned, a session created with the profile stored, the handler registered, and `created {sessionId}` returned; the client can then `attach`.
- [ ] 6.2 Test create-with-unknown-profile: actionable error returned, registry count unchanged, no orphaned core (5.1 / spec "No handler leaked on rejection").
- [ ] 6.3 Test create-at-cap: actionable cap error, spawned core torn down, no registry entry; and that reattach to an existing session is exempt from the cap.

## 7. Close-out

- [ ] 7.1 Tick `remote-session-gateway` tasks 10.1 and 10.2 in that change's `tasks.md` (or annotate them as completed by `per-session-profile-selection`), per the deferral note.
- [ ] 7.2 `make build` clean (TreatWarningsAsErrors), `make test` green, `openspec validate per-session-profile-selection --strict` passes.
