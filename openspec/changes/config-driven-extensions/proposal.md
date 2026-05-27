## Why

After ADR-008, a restarted `dmoncore` starts with **no** extensions: `extension.load` is unimplemented (`NullExtensionHandler`) and nothing persists which extensions were active. ADR-009 decides that active extensions are declared in config and loaded at startup, with `/reload` (a process restart) re-reading that config. This gives the project a single, durable definition of "active extensions" and makes restart-as-reload — the mechanism ADR-008 relies on — actually usable.

## What Changes

- Add an `extensions` list to `config.yaml` at both scopes: project (`./.dmon/config.yaml`) and user (`~/.dmon/config.yaml`). Each entry is an extension source (`nuget:…`, an assembly path, or a `.csx` path) with optional per-entry settings.
- At `dmoncore` startup, compute the **effective set** = union of user + project lists, deduplicated by normalized source (project wins for per-entry settings), and load each through `ExtensionService` in a deterministic order.
- **Config presence implies trust:** extensions listed in config load at startup **without** re-prompting. The ADR-006 gate applies when a source is *added* to config, not on every startup.
- Define the add/remove surface (**edit-only**, no ephemeral tier): a source becomes active by being written to a config scope and reloading; it is removed by deleting it from config. Reconcile `extension.load` / `ExtensionLoadTool` with this model (replace the `NullExtensionHandler` runtime-load assumption).
- Add `/reload` to the terminal host: `CoreProcessManager.RestartAsync` stops the current core, spawns a fresh one, re-binds stdio, and re-opens the active session directory. Restart occurs strictly between turns.
- **BREAKING (workflow):** there is no in-memory-only extension load. Activating an extension requires a config entry + reload.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `extension-model`: extensions are declared in `config.yaml` at user + project scope and loaded at startup as the union of both lists; config presence implies trust (no per-startup prompt); there is no ephemeral runtime-load tier.
- `terminal-host`: gains a `/reload` command that restarts the core process, re-binds stdio, and re-opens the active session so config changes (and refreshed extension assemblies) take effect.

## Impact

- **`src/Dmon.Core/Bootstrap/BootstrapService.cs`** (and/or a startup loader): read the effective `extensions` set and load each at startup.
- **`config.yaml` schema** (both scopes): new `extensions` list; default-config template updated.
- **`src/Dmon.Core/Rpc/` extension handler**: replace `NullExtensionHandler` with the config-write/reload surface decided here; reconcile `ExtensionLoadTool` guidance.
- **`src/Dmon.Terminal/CoreProcessManager.cs`** + terminal stdio/session wiring + `SlashCommandParser`: `/reload` and `RestartAsync`.
- **Permission model interaction:** ADR-006 gate fires at add-time, not startup (refines ADR-002 per ADR-009). No new RPC message *shapes* beyond what `/reload` and config-write require.
- **Depends on:** `extension-default-load-context` (ADR-008). **Backed by:** ADR-009.
