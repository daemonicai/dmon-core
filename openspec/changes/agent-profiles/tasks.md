## 1. Profile contracts (Dmon.Abstractions)

- [x] 1.1 Add a `PermissionMode` enum (`Coding`, `Sandbox`) in `Dmon.Abstractions`.
- [x] 1.2 Add an immutable `AgentProfile` record: `Name`, `Persona` (string), `Assets` (bool), `PermissionMode`.
- [x] 1.3 Add `IAgentProfileResolver` with a single `Task<AgentProfile> ResolveAsync(string? requestedProfile, CancellationToken)` returning the session's resolved profile.

## 2. Profile config + two-scope merge (Dmon.Core/Config)

- [x] 2.1 Define the `profiles:` config entry shape (`persona` | `personaFile`, `assets`, `permissionMode`) and a `defaultProfile` key.
- [x] 2.2 Add a profile config reader that reads both `~/.dmon/config.yaml` and `./.dmon/config.yaml` explicitly (not via `IConfiguration` array layering), mirroring `EffectiveExtensionSetReader`.
- [x] 2.3 Compute the effective profile set as a union deduplicated by name with project scope winning per-name conflicts (ADR-009 rule).
- [x] 2.4 Resolve `personaFile` paths relative to the scope that declared the entry.

## 3. Built-in coding profile + resolver

- [x] 3.1 Lift the current `StaticCore` text into a built-in `coding` profile (`persona` = the text verbatim, `assets: false`, `permissionMode: Coding`).
- [x] 3.2 Implement `IAgentProfileResolver`: merge effective set with the built-in, apply precedence (per-session `profile` > `defaultProfile` > built-in `coding`), read inline/file persona, return an immutable `AgentProfile`.
- [x] 3.3 Fail with an actionable error when a selected profile name matches nothing in the effective set (no silent fallback).
- [x] 3.4 Reject the incoherent `permissionMode: sandbox` + `assets: false` combination at resolution time with an actionable config error.
- [x] 3.5 Register the resolver in DI so the `AgentProfile` is resolved once per session and shared by all consumers.

## 4. System prompt persona injection (Dmon.Core/SystemPrompt)

- [x] 4.1 Replace the `StaticCore` constant usage in `SystemPromptBuilder` with the active `AgentProfile.Persona`.
- [x] 4.2 Surface the per-session asset directory in the dynamic-context block only when `AgentProfile.Assets` is true; omit it otherwise.
- [x] 4.3 Confirm the surrounding scaffolding (project `CLAUDE.md`/`AGENTS.md` discovery, dynamic context, system-message-at-index-0) is otherwise unchanged.

## 5. Permission mode in the gate (Dmon.Core permission pipeline)

- [x] 5.1 Thread the session's `PermissionMode` and asset directory path to the write/edit/delete evaluators.
- [x] 5.2 Under `Sandbox`, return an implicit allow (risk `none`) when a target's normalised path (symlinks resolved, `..` collapsed) is within `assets/<session_id>/`.
- [x] 5.3 Ensure operations outside the asset subtree evaluate exactly as in `Coding` mode, and that the denylist is checked before the sandbox allowance and cannot be overridden.

## 6. Asset directory provisioning

- [ ] 6.1 At session creation, provision `assets/<session_id>/` under the workspace root when `AgentProfile.Assets` is true; create nothing when false.
- [ ] 6.2 Keep the asset directory distinct from the session-storage `attachments/` and independent of the session process lifetime.

## 7. Tests and documentation

- [ ] 7.1 Test: under the `coding` profile, the assembled persona equals the prior static core byte-for-byte, no asset dir is created, and permission mode is `coding`.
- [ ] 7.2 Test: config-defined profiles merge across scopes (union, dedupe by name, project wins); `personaFile` is read; unknown name and sandbox+assets:false both error.
- [ ] 7.3 Test: `sandbox` allows writes within `assets/<session_id>/`, prompts outside it, and never overrides the denylist (including symlink/`..` escape attempts).
- [ ] 7.4 Test: per-session `profile` overrides `defaultProfile`; absent both, built-in `coding` applies.
- [ ] 7.5 Update `config.yaml` documentation/sample with the `profiles:` and `defaultProfile` keys and the two permission modes.
