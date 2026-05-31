## Why

`dmoncore` has exactly one persona: `SystemPromptBuilder` hardcodes a coding-agent identity, tool norms, and permission language in a `StaticCore` constant. A second consumer (the personal-assistant gateway, ADR-012) needs a different persona, a per-session output directory, and a less restrictive write posture — and the coding profile must keep ADR-006's confirm-every-write behaviour intact. Persona, asset provisioning, and permission posture change together, so they must be selected together. ADR-013 (Accepted) records this decision; this change implements it.

## What Changes

- Introduce an **agent profile**: a named, session-fixed bundle of (a) a **persona** (the system-prompt identity/norms block), (b) an **`assets` flag** (default off) controlling provisioning of a per-session `assets/<session_id>/` directory under the workspace root, and (c) a **permission mode**.
- Add two code-level **permission modes**: `coding` (ADR-006 exactly as written) and `sandbox` (ADR-006 plus implicit-write to the session's own `assets/<session_id>/` subtree, mirroring implicit-read-within-CWD). The denylist and gate placement are unchanged.
- Replace `SystemPromptBuilder`'s `StaticCore` constant with a **profile-resolved persona**. The surrounding scaffolding (dynamic session context, project `CLAUDE.md`/`AGENTS.md` config) is unchanged, except the session-context section surfaces the asset directory only when `assets` is on.
- Ship **one built-in profile, `coding`** — the current `StaticCore` verbatim + `assets` off + `coding` mode — as the default. Default behaviour is preserved byte-for-byte. **Not BREAKING.**
- Define additional profiles in `config.yaml` under a `profiles:` map at both ADR-009 scopes (user + project), union/project-wins merge. Each entry carries `persona` (inline text or a `personaFile` path), `assets: bool`, and `permissionMode: coding | sandbox`.
- Select a profile via `defaultProfile` in config plus an optional per-session `profile` override at session creation (per-session wins). An **unknown profile name is a hard, actionable error** — never a silent fallback. A profile is **immutable for a session's lifetime**.
- Add an `IAgentProfileResolver` producing an `AgentProfile` record, resolved **once per session** and consumed by the prompt builder, the asset-provisioning step, and the permission-mode selection — so the three cannot drift.
- Out of scope here: the network gateway (separate `remote-session-gateway` change); per-profile model/toolset pinning and persona composition (deferred Open Questions in ADR-013).

## Capabilities

### New Capabilities
- `agent-profiles`: the profile bundle (persona + assets toggle + permission mode), its config-driven definition and two-scope merge, per-session selection with the built-in `coding` default, the immutable-per-session rule, the `IAgentProfileResolver`/`AgentProfile` contract, and per-session asset-directory provisioning.

### Modified Capabilities
- `system-prompt`: the persona block becomes a resolved profile input rather than a hardcoded constant; the asset-directory line is surfaced in session context only under an assets-enabled profile.
- `permission-model`: adds the `sandbox` permission mode (a profile-scoped implicit-write zone over the session's own asset directory). The `coding` mode, denylist, grant precedence, and gate placement are unchanged.

## Impact

- **Code:** `src/Dmon.Core/SystemPrompt/SystemPromptBuilder.cs` (persona becomes injected), `src/Dmon.Abstractions` (new `IAgentProfileResolver` + `AgentProfile`), `src/Dmon.Core/Config` (profile config reader + two-scope merge, mirroring `EffectiveExtensionSetResolver`), the permission gate in the `IChatClient` middleware pipeline (mode selection + sandbox implicit-write), and session-creation/bootstrap (resolve profile once, provision the asset dir when enabled).
- **Config:** new `profiles:` and `defaultProfile:` keys in `config.yaml` (ADR-009 scopes).
- **Protocol:** an optional, additive `profile` parameter on session creation (consumed fully by `remote-session-gateway`; the terminal host may expose it as a launch flag).
- **ADRs:** implements ADR-013; refines ADR-006 (`sandbox` mode); uses ADR-009 config model; no ADR is superseded.
- **Backward compatibility:** absent config, the built-in `coding` profile reproduces today's behaviour exactly.
