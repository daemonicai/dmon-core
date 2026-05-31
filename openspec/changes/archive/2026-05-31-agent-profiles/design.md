## Context

Today `SystemPromptBuilder` (`src/Dmon.Core/SystemPrompt/SystemPromptBuilder.cs`) hardcodes the persona in a `StaticCore` constant and assembles dynamic context + project config around it. Permission behaviour is enforced in the `IChatClient` permission gate (`PermissionGateChatClient`), which delegates per-tool evaluation to each tool's `Evaluate` (e.g. `WriteFileTool.Evaluate`) using `IPermissionPolicy` settings. Config is layered YAML at `~/.dmon/config.yaml` (user) and `./.dmon/config.yaml` (project); `EffectiveExtensionSetResolver` already implements the union/project-wins merge ADR-009 mandates.

ADR-013 (Accepted) defines an **agent profile** as a session-fixed bundle of persona + an `assets` toggle + a permission mode, and draws the load-bearing line: **persona is config, permission behaviour is code.** This change implements ADR-013's core capability; the network gateway that selects profiles per session is a separate change (`remote-session-gateway`).

## Goals / Non-Goals

**Goals:**
- Make the persona selectable without changing default behaviour (built-in `coding` profile = today, byte-for-byte).
- Add a `sandbox` permission mode whose only delta from `coding` is an implicit-write zone over the session's own asset directory.
- Define and merge profiles via the existing two-scope config model; select via `defaultProfile` + a per-session override.
- Resolve the profile once per session and feed all three consumers from one `AgentProfile`, so persona, assets, and permission mode cannot drift.

**Non-Goals:**
- The network gateway, `createSession` RPC, and per-session selection over the wire (the `profile` parameter is defined as additive but exercised by `remote-session-gateway`).
- Per-profile model/provider or toolset pinning (ADR-013 Open Question A — deferred).
- Persona composition / inheritance — V1 is full replacement of the persona block (ADR-013 Open Question B — deferred).
- Mutable mid-session profile switching (explicitly rejected by ADR-013).

## Decisions

**D1 — Persona is an injected input, not a builder subclass.** `SystemPromptBuilder` keeps its single implementation; the `StaticCore` constant becomes the built-in `coding` profile's persona, and the builder consumes the active `AgentProfile.Persona`. *Alternative — multiple `ISystemPromptBuilder` implementations by DI — rejected (ADR-013 Alternatives): it duplicates the shared scaffolding for every persona when only the persona block varies.*

**D2 — Permission mode is a fixed core enum, surfaced to evaluators; not config text.** The active mode (`coding` | `sandbox`) is resolved per session and made available to the file-write evaluators alongside the session's asset path. `sandbox` adds one allowance: a normalised target within `assets/<session_id>/` returns an implicit allow. *Alternative — user-authored permission rules per profile — rejected (ADR-013 Alternatives): behavioural permission changes must stay in code behind a small enum so ADR-006's posture and the non-overridable denylist cannot be weakened from config.*

**D3 — Profile config + merge reuses the ADR-009 pattern.** A profile config reader mirrors `EffectiveExtensionSetReader`/`Resolver`: read both scopes explicitly (not via `IConfiguration` array layering), union, dedupe by name, project wins. `personaFile` paths resolve relative to the scope that declared them.

**D4 — `IAgentProfileResolver` → `AgentProfile`, resolved once per session.** The resolver (in `Dmon.Abstractions`) merges the effective profile set, applies selection precedence (`per-session` > `defaultProfile` > built-in `coding`), validates the name (hard error on miss), reads the persona (inline or file), and returns an immutable `AgentProfile` record consumed by the prompt builder, the asset-provisioning step, and the gate. Single resolution is the mechanism that prevents drift.

**D5 — Asset directory provisioning is gated on `AgentProfile.Assets` at session start.** When true, create `assets/<session_id>/` under the workspace root; it is distinct from session-storage `attachments/` (ADR-004) and is not deleted with the session process. When false, nothing is created. (Reaping/durability across a detached handler is the gateway change's concern; here, provisioning + the `sandbox` write zone are what land.)

## Risks / Trade-offs

- **[Persona drift from the spec's byte-for-byte guarantee]** → Lift the existing `StaticCore` text into the `coding` profile *verbatim*; add a test asserting the assembled `coding` persona equals the prior constant so any future edit is deliberate.
- **[Sandbox path-escape via symlinks / `..`]** → The implicit-write check MUST use the same normalisation (symlinks resolved, `..` collapsed) ADR-006 mandates for path grants, and MUST run after the denylist; a target only qualifies if its normalised path is genuinely within `assets/<session_id>/`.
- **[A profile that sets `permissionMode: sandbox` but `assets: false`]** → Incoherent (sandbox's only delta references the asset dir). The resolver SHALL reject this combination at resolution time as an actionable config error, rather than silently degrading to `coding`.
- **[Config schema sprawl]** → Keep the `profiles:` entry to exactly the three fields ADR-013 defines; defer model/toolset pinning (Open Question A) so the schema does not grow ahead of decisions.
