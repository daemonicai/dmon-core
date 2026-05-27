## Context

`dmoncore` reads layered YAML config in `Program.cs`: `./.dmon/config.yaml` (project) then `~/.dmon/config.yaml` (user), both optional. `BootstrapService` owns `.dmon` directory creation and writes the default config template. `PermissionSettingsLoader` already loads project and user `settings.yaml` separately and merges with explicit precedence — the precedent for two-scope loading. Sessions are durable on disk (ADR-004). The `Dmon.Terminal` host spawns and supervises the core via `CoreProcessManager` (which has `StartAsync`/`StopAsync`, no restart).

Two gaps block ADR-009: `extension.load` is `NullExtensionHandler` (unimplemented), and there is no startup extension-load step. This change implements ADR-009 on top of ADR-008's Default-context loading.

## Goals / Non-Goals

**Goals:**
- A single startup load path that loads the effective (user ∪ project) `extensions` set through `ExtensionService`.
- An edit-only add/remove model with no ephemeral tier.
- `/reload` = restart that re-reads config and re-opens the active session.
- Config presence implies trust (no per-startup prompt); the ADR-006 gate fires at add time.

**Non-Goals:**
- Hot-reload without restart (ADR-008).
- Supporting conflicting dependency versions (ADR-008, first-writer-wins).
- NuGet package *resolution* (deferred to V1.1 per ADR-002).
- A GUI/desktop reload affordance (terminal host only for V1).

## Decisions

### D1 — `extensions` schema and union semantics
Each scope's `config.yaml` may contain:

```yaml
extensions:
  - source: "nuget:Acme.Tools"      # or "./ext/Foo.dll" or "./scripts/bar.csx"
    # optional per-entry settings (reserved; e.g. version pin)
```

Startup reads **both files' `extensions` arrays explicitly** and unions them, deduplicating by normalized source (case/trailing-slash normalized for paths; id+version for nuget). Deduplication keeps the project entry's per-entry settings. The union is computed in code, not via `IConfiguration` array layering (which replaces arrays by index and would silently drop entries).

**Alternative considered:** rely on `IConfiguration` to merge. Rejected — arrays do not merge across providers; the user list would be clobbered by the project list.

### D2 — Deterministic load order
Load user entries first, then project entries, each in file order, after dedup. This makes ADR-008's first-writer-wins predictable: a dependency version pulled by an earlier extension wins for later ones.

### D3 — Startup loading lives in bootstrap
A startup step (in or invoked by `BootstrapService`) resolves the effective set and calls `ExtensionService.LoadAsync` per entry, **bypassing the interactive confirm callback** — config presence is the prior approval (D5). Failures are logged per-entry and do not abort startup (consistent with the existing all-or-nothing-per-extension behaviour); a failed extension is simply absent until fixed.

### D4 — Add/remove surface (edit-only)
Activating a source = writing it to a config scope, then reloading. This change provides:
- Manual edit of `config.yaml` (always valid).
- A core operation to **append a source to a chosen scope's `extensions` list** (so the agent's `extension.analyze`→install flow has a target). This operation writes config and reports that a reload is required; it does **not** load into the running process. `NullExtensionHandler` is replaced by a handler implementing this write-and-advise behaviour. `ExtensionLoadTool` guidance is updated to "add to config, then `/reload`".

**Alternative considered:** `extension.load` performs a live in-memory load. Rejected — reintroduces the ephemeral tier ADR-009 excludes and creates drift between running state and config.

### D5 — Trust model: config presence = approval
Extensions in config load at startup without an ADR-006 prompt. The gate fires when a source is **added** (D4's write operation runs the security analysis / approval before writing). Removing the entry revokes trust. This is the concrete meaning of ADR-002's "approved at project/global scope" (ADR-009 §5).

### D6 — `/reload` and restart
`CoreProcessManager.RestartAsync`: `StopAsync()` the current process, spawn a fresh one via the existing start logic, re-bind the terminal's stdio read/write loop to the new `StandardOutput`/`StandardInput`, and re-open the active session directory so history continues. `/reload` is parsed by `SlashCommandParser` and only runs between turns. The fresh core re-reads config and reloads the effective set.

## Risks / Trade-offs

- **[Running state vs config drift]** A user edits config mid-session; the running process is stale until reload. → Acceptable and expected; the running set always reflects the last reload. Optionally surface "config changed — `/reload` to apply".
- **[Startup latency]** Each declared extension is resolved/loaded at every start/reload. → Bounded by the expected handful; revisit lazy loading if sets grow.
- **[Trust bypass at startup]** Loading config entries without prompting trusts whatever is in config. → Intentional (D5); the write operation is the gated step, and config files are user-owned. Document that hand-editing config to add an extension is an explicit trust act.
- **[stdio re-bind correctness]** The terminal's read/write loop must cleanly detach from the dead process and attach to the new one without dropping or duplicating events. → Reuse the startup wiring path; add an integration test for restart-between-turns preserving the session.

## Migration Plan

1. Land after `extension-default-load-context`.
2. Existing installs have no `extensions` key → effective set is empty → behaviour unchanged until a user adds entries.
3. Rollback: remove startup loading + `/reload`; revert `NullExtensionHandler`. Config `extensions` keys become inert. No persisted session state depends on this change.

## Open Questions

- Should the config-write operation (D4) optionally take effect immediately by *also* triggering a `/reload`, or always require an explicit reload? (Leaning: explicit reload, to keep "config = what loads at startup" unambiguous.)
- Should a changed `config.yaml` be detected at runtime to prompt "`/reload` to apply", or is that out of scope for V1? (Leaning: out of scope; revisit.)
