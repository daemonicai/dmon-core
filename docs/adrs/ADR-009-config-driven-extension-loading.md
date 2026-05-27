# ADR-009: Config-Driven Extension Loading

**Date:** 2026-05-27
**Status:** Accepted

## Context

ADR-008 establishes that extensions load into the Default `AssemblyLoadContext` and that reclaiming/refreshing extension code is done by restarting the `dmoncore` child process. That leaves two questions open: **what is the set of "active" extensions a fresh process should load**, and **how does a user trigger a reload**.

Today there is no answer: `extension.load` is routed to `NullExtensionHandler` (unimplemented), and nothing persists or replays which extensions were loaded. A restart would therefore start with no extensions. ADR-002 anticipated a persistence story — *"the load action prompts each time unless the user approves the source at project or global scope"* — but never defined what "approved at scope" concretely is.

dmon already has the ingredients: layered YAML config at `./.dmon/config.yaml` (project) and `~/.dmon/config.yaml` (user), a `BootstrapService` that owns `.dmon` setup, and `PermissionSettingsLoader` as a precedent for two-scope loading. Session state is durable on disk (ADR-004), so a restart re-attaches to the same conversation.

## Decision

Active extensions are **declared in config** and loaded at `dmoncore` startup. There is no ephemeral runtime-load tier.

1. **Config is the source of truth.** Both `config.yaml` scopes gain an `extensions` list. The effective set is the **union** of the user and project lists, deduplicated by normalized source. Where the same source appears at both scopes, the project entry wins for any per-entry settings. (The union is computed explicitly by reading both files — not via `IConfiguration` array layering, which replaces arrays by index.)

2. **A single load path: startup.** `BootstrapService` (or a startup step it invokes) reads the effective `extensions` set and loads each through `ExtensionService`. There is no dynamic in-memory-only load — being loaded and being in config are the same thing (**edit-only**).

3. **Adding/removing an extension is a config edit followed by a reload.** A source enters the active set by being written to a config scope (by manual edit, or by a command that writes config); it leaves by being removed from config. Changes take effect on the next reload.

4. **`/reload` is a process restart.** The terminal host exposes `/reload`, which calls `CoreProcessManager.RestartAsync`: stop the current core, spawn a fresh one (which re-reads config and reloads the effective set), re-bind stdio, and re-open the active session directory so the conversation continues. Restart happens strictly between turns.

5. **Config presence implies trust.** This is the concrete meaning of ADR-002's "approved at project/global scope": an extension in config is a previously-approved source and loads at startup **without** re-prompting. The ADR-006 permission/analysis gate fires at **add time** (when the source is written to config), not on every startup. Removing the entry revokes the trust.

## Consequences

- **One mental model, one load path.** Reasoning about "what's loaded" reduces to "what's in the merged config." `/reload` and cold start are the same operation.
- **No try-before-you-commit.** Trialling an extension means editing config and reloading; there is no transient "load once" that disappears on restart. Accepted for simplicity; revisitable if friction appears.
- **`extension.load` must be reconciled.** The current `NullExtensionHandler` and the `extension.analyze`→"use extension.load" agent flow (`ExtensionLoadTool`) must be redefined in terms of config writes + reload rather than ephemeral runtime loading. The exact command surface (manual edit only vs. an `extension.add`/install command that writes config at a chosen scope) is settled in the `config-driven-extensions` change.
- **Startup cost scales with extension count.** Each declared extension is resolved and loaded at every start/reload. Acceptable for the expected handful; large sets would make reload slower.
- **Conflicting dependency versions remain unsupported** (ADR-008, first-writer-wins). Load order within the merged set should be deterministic (e.g. user-then-project, stable) so first-writer-wins is predictable.
- **Refines ADR-002 and ADR-006; supersedes neither.** It defines "approve at scope" as a config entry and locates the permission gate at add time. The `IDmonExtension`/`AIFunction` contract and the conservative permission posture are unchanged.

## Relationship to other ADRs

- **ADR-008** (loading mechanism) is the prerequisite: Default-context loading and restart-as-reclaim are what make a config-driven reload coherent.
- **ADR-002** (extension contract) is refined, not changed: this ADR gives "approved at project/global scope" a concrete representation.
- **ADR-004** (session storage) is relied upon: durable sessions are why restart can preserve the conversation.
