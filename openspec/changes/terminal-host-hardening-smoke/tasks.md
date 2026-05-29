## 1. Preconditions (external — gate the smoke)

- [ ] 1.1 `Dmon.Core` starts without the MCP/`Microsoft.Extensions.AI` crash and reaches a ready state (`build/dmon` launches into an interactive session).
- [ ] 1.2 A provider API key is configured per ADR-005 (e.g. `ANTHROPIC_API_KEY`) so the core can complete a turn.

## 2. Live smoke run

- [ ] 2.1 `make build && build/dmon`; exercise the no-LLM recipe from the archived `2026-05-28-dmon-migration` DEVLOG (typing, `/reload` end-to-end, Ctrl+C).
- [ ] 2.2 Confirm **(a)** `── goodbye ──` renders on Ctrl+C exit, while the `dcli` fixed region is still alive.
- [ ] 2.3 Confirm **(b)** mashing `/reload` twice in quick succession during the restart window produces exactly one `[Reload] Core restarted.` line, not two.
- [ ] 2.4 Record the result here (and in the DEVLOG). If a defect is found, open a new fix change and do not tick 2.2/2.3 until the behavior is correct.

## 3. Close out

- [ ] 3.1 `openspec validate terminal-host-hardening-smoke --strict`; propose `/opsx:archive terminal-host-hardening-smoke` and wait for user confirmation.
