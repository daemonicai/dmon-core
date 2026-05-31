# ADR-013: Agent Profiles — Selectable Persona, Asset Workspace, and Permission Mode

**Date:** 2026-05-31
**Status:** Accepted

## Context

`dmoncore` has exactly one persona today: `SystemPromptBuilder` (`src/Dmon.Core/SystemPrompt/SystemPromptBuilder.cs`) hardcodes a coding-agent identity, tool norms, and permission language in a `StaticCore` string constant. Everything else the builder does — appending dynamic session context (CWD, OS, provider, loaded extensions) and the project's `CLAUDE.md`/`AGENTS.md` config — is **persona-agnostic scaffolding**. There is already an injection seam, `ISystemPromptBuilder` in `Dmon.Abstractions`.

Two requirements break the single-persona assumption:

- The coding persona is **no longer the only one**. A consumer (the iOS-facing gateway of ADR-012) wants a **personal-assistant** persona, and others will follow. The system prompt must be selectable.
- That assistant persona wants a per-session **`assets/<session_id>/` output directory** (resolved with ADR-012's Open Question C), where it writes generated artifacts freely — but a *coding* session wants **no such directory** and wants ADR-006's confirm-every-write posture intact.

These are not two independent toggles. A coding persona whose prompt says "writes require confirmation" combined with an asset directory that silently allows writes is an incoherent pairing. Persona, asset provisioning, and permission posture **change together**, so they should be selected together. This ADR introduces that bundle.

A constraint from the code shapes the design: the persona is just text and can come from config, but **permission behaviour is enforced in the `IChatClient` permission gate** (ADR-002/ADR-006) — it is code, not text, and must not become user-authored content that could weaken ADR-006's posture.

## Decision

1. **An agent profile is a named bundle, fixed at session creation.** A profile comprises: (a) a **persona** — the system-prompt identity/norms block; (b) an **`assets` flag** — whether a per-session `assets/<session_id>/` directory is provisioned (**default off**); and (c) a **permission mode** (Decision 3). The profile is selected per session and is **immutable for that session's lifetime** — switching persona means starting a new session, because conversation history accretes under one persona and one permission posture.

2. **Persona replaces the hardcoded `StaticCore`.** `SystemPromptBuilder` is refactored so the persona block is a **resolved profile input**, not a constant. The surrounding scaffolding (dynamic session context, project `CLAUDE.md`/`AGENTS.md` config) stays shared and persona-agnostic — except that the session-context section surfaces the asset directory **only when `assets` is on**. Today's coding persona text moves **verbatim** from the `StaticCore` constant into the built-in `coding` profile, so default behaviour is preserved byte-for-byte.

3. **Core provides permission modes; profiles select one.** Two code-level permission modes ship:
   - **`coding`** — ADR-006 exactly as written: no implicit writes anywhere, confirm every novel operation.
   - **`sandbox`** — ADR-006 plus exactly one addition: the session's own `assets/<session_id>/` subtree is **implicit-write** (`risk: none`), mirroring ADR-006's implicit-read-within-CWD. Everything outside that subtree, and the entire hardcoded denylist, behave precisely as ADR-006 specifies.

   Permission modes are a **fixed core enum**, not config text, because they are behavioural and enforced in the permission gate (ADR-002 middleware). User config selects a mode; it cannot author one.

4. **Profiles are config-defined, with one built-in.** Core ships exactly one built-in profile — **`coding`** (coding persona + `assets: false` + `coding` permission mode) — which is the default when nothing is selected, preserving today's behaviour. Additional profiles are declared in `config.yaml` under a `profiles:` map at both ADR-009 scopes (user `~/.dmon`, project `./.dmon`), merged by the **same union / project-wins rule** ADR-009 uses for extensions. A profile entry carries `persona` (inline text **or** a `personaFile` path), `assets: bool`, and `permissionMode: coding | sandbox`. dmoncore stays **persona-agnostic**: a personal-assistant persona is *deployment configuration*, not opinion shipped in the core.

5. **Selection is a deployment default plus a per-session override.** `config.yaml` may set `defaultProfile: <name>`. Session creation may override it with a `profile` parameter — this is the field ADR-012's `createSession` carries, and the terminal host may expose it as a launch flag. **Per-session wins**; absent both, the built-in `coding` profile applies. An **unknown profile name is a hard, actionable error** at session start — never a silent fallback.

6. **The asset directory is durable and profile-gated.** When `assets` is on, session creation provisions `assets/<session_id>/` under the workspace root: it is the session's implicit-write zone (Decision 3) and the predictable, user-retrievable home for generated artifacts. It is **distinct** from the session storage directory's internal `attachments/` (ADR-004), and it **outlives the session handler** — ADR-012 Decision 7 reaps the `dmoncore` process, never the assets. When `assets` is off (the coding default), no such directory is created.

7. **Profile resolution is session-scoped and single-sourced.** A resolver (`IAgentProfileResolver` producing an `AgentProfile` record) resolves the active profile **once per session** and feeds all three consumers — `SystemPromptBuilder` (persona + the asset-dir context line), the asset-provisioning step, and the permission-mode selection in the gate. One resolution per session guarantees the three cannot drift out of agreement.

## Consequences

- **Default behaviour is unchanged.** The `coding` persona and ADR-006 permissions are preserved byte-for-byte; no asset directory is created unless a non-default profile asks for one.
- **dmoncore becomes multi-persona without becoming multi-purpose in core.** Personas live in config; only the two behavioural permission modes and the asset-provisioning capability are code. The "coding agent" framing of the project is intact — a personal assistant is a configuration of the same core.
- **The incoherent-combination risk is closed.** Persona, assets, and permission mode are chosen as one unit; you cannot set a coding persona that claims writes are confirmed alongside a silently-writable sandbox.
- **Generative prompt-fatigue is solved** by the `sandbox` mode's implicit-write zone — a personal assistant emits artifacts without a confirmation on every file.
- **Resolves ADR-012 Open Question C** at the profile level: session creation selects a profile, which decides persona, asset provisioning, and permission mode.
- **New surfaces:** a `profiles` / `defaultProfile` config section (ADR-009 scopes) and a small additive `profile` parameter on session creation (ADR-003). No existing message or behaviour is reshaped.

## Alternatives

- **Two independent knobs** — a `systemPrompt` string field plus an `assets: bool` flag, with no bundling. Rejected: it permits incoherent combinations (Decision 1's pairing problem) and scatters the coding-vs-assistant distinction across unrelated settings with no single place that says "this is what an assistant session is."
- **Multiple full `ISystemPromptBuilder` implementations selected by DI.** Rejected: it duplicates the shared scaffolding (dynamic context, project config) in every persona, when the only variation is the persona block plus flags. Composition beats subtyping here.
- **Free-form, user-authored permission rules per profile instead of named modes.** Rejected for V1: behavioural permission changes belong in code behind a small enum, not in config text that could silently weaken ADR-006. The denylist and gate placement must stay non-negotiable.
- **Mutable mid-session profile switching.** Rejected: history is accreted under one persona and one permission posture; a switch is a new session, not a setting change.

## Open Questions

- **A. Per-profile model / toolset.** A profile could also pin a provider/model or an active-extension subset. Deferred — V1 profiles cover persona + assets + permission mode only; model and tooling stay governed by the provider registry (ADR-007) and config-driven extension loading (ADR-009).
- **B. Persona composition.** Whether a user persona may *extend* (append to) the built-in core rather than fully replace the persona block. V1 is full replacement of the persona block; the shared scaffolding is always present regardless.

## Relationship to other ADRs

- **ADR-002** — permission modes are enforced in the existing `IChatClient` permission gate; no contract or pipeline change, only a per-session mode selection.
- **ADR-003** — session creation gains an optional, additive `profile` parameter; no existing message is reshaped.
- **ADR-004** — session storage is unchanged. The `assets/<session_id>/` directory is a separate, profile-gated, durable per-session location, distinct from the storage directory's internal `attachments/`.
- **ADR-006** — refined, supersedes nothing: adds the `sandbox` permission mode (a profile-scoped implicit-write zone over the session's own asset dir). The `coding` mode, the denylist, grant precedence, and gate placement are all unchanged.
- **ADR-007 / ADR-009** — profiles use the same two-scope `config.yaml` union/project-wins merge as extensions; config presence is the source of truth. Provider/model selection is unchanged (Open Question A).
- **ADR-012** — resolves Open Question C: `createSession` selects a profile, which decides persona, asset provisioning, and permission mode; the asset directory outlives the reaped handler (ADR-012 Decision 7).
