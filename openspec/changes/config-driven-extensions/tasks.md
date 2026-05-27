## 1. Config Schema

- [x] 1.1 Define the `extensions` list schema for `config.yaml` (entry = `source` + optional per-entry settings)
- [x] 1.2 Update the default-config template in `BootstrapService` with a commented `extensions` example
- [x] 1.3 Add a typed model + reader for the `extensions` list at a single scope

## 2. Effective Set Resolution

- [x] 2.1 Read both project and user `extensions` lists explicitly (not via IConfiguration array layering)
- [x] 2.2 Union + deduplicate by normalized source (project wins for per-entry settings)
- [x] 2.3 Produce a deterministic load order (user entries then project entries, each in file order)
- [x] 2.4 Unit test: union/dedup/precedence and empty-config cases

## 3. Startup Loading

- [x] 3.1 Add a startup step (in/invoked by `BootstrapService`) that loads each effective entry via `ExtensionService`, bypassing the interactive confirm callback
- [x] 3.2 Log per-entry load failures and continue startup (no abort)
- [x] 3.3 Verify config-declared extensions register their tools at startup with no prompt
- [x] 3.4 Integration test: one failing entry is skipped; the rest load and the daemon starts

## 4. Add/Remove Surface (Edit-Only)

- [ ] 4.1 Replace `NullExtensionHandler` with a handler that appends a source to a chosen config scope's `extensions` list and reports "reload required"
- [ ] 4.2 Run the ADR-006 add-time gate (security analysis / approval) before writing the entry
- [ ] 4.3 Update `ExtensionLoadTool` guidance to "add to config, then `/reload`" (no ephemeral runtime load)
- [ ] 4.4 Unit test: add writes the entry to the correct scope and does not load into the running process

## 5. Terminal /reload + Restart

- [ ] 5.1 Add `RestartAsync` to `CoreProcessManager` (stop current process, spawn a fresh one)
- [ ] 5.2 Re-bind the terminal's stdio read/write loop to the new process's `StandardOutput`/`StandardInput`
- [ ] 5.3 Re-open the active session directory against the new process after restart
- [ ] 5.4 Add `/reload` to `SlashCommandParser`; guard it to run only between turns
- [ ] 5.5 Integration test: `/reload` produces a fresh process, re-binds stdio, and the active session re-opens with prior history intact
- [ ] 5.6 Integration test: edited `config.yaml` is reflected in the effective set after `/reload`

## 6. Docs & ADR Cross-Reference

- [ ] 6.1 Document the `extensions` config schema, the edit-only model, and `/reload` in the config/usage docs
- [ ] 6.2 Reference ADR-009 (and its relationship to ADR-002/ADR-006/ADR-008) in the docs
