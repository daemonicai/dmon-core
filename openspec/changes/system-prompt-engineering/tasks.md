## 1. Interface and abstractions

- [ ] 1.1 Define `ISystemPromptBuilder` interface in `Dmon.Abstractions` with a single `BuildAsync(CancellationToken) -> Task<ChatMessage>` method
- [ ] 1.2 Add `system.notice` event type to `Dmon.Protocol` (reuse `SystemEvents` or add new event record)

## 2. Config discovery

- [ ] 2.1 Implement config file resolution logic: `~/.dmon/AGENTS.md`, `{CWD}/AGENTS.md`, `{CWD}/CLAUDE.md` fallback
- [ ] 2.2 Combine user-level and project-level configs when both present (user config first)
- [ ] 2.3 Emit `system.notice` event when `CLAUDE.md` is used as fallback

## 3. System prompt builder implementation

- [ ] 3.1 Implement `SystemPromptBuilder` in `Dmon.Core` implementing `ISystemPromptBuilder`
- [ ] 3.2 Write static core text (identity, tool-usage norms, permission model awareness, terse/informal tone)
- [ ] 3.3 Assemble dynamic context block (CWD, OS/platform, provider name + model ID, loaded extensions)
- [ ] 3.4 Append project config content (from step 2) when present
- [ ] 3.5 Register `SystemPromptBuilder` in DI container

## 4. TurnHandler integration

- [ ] 4.1 Inject `ISystemPromptBuilder` into `TurnHandler` constructor
- [ ] 4.2 On first `turn.submit`, call `BuildAsync`, prepend result as `_history[0]`
- [ ] 4.3 Skip rebuild on subsequent turns (check `_history.Count > 0` or a flag)

## 5. Tests

- [ ] 5.1 Unit test `SystemPromptBuilder` with no config files present
- [ ] 5.2 Unit test config discovery: `AGENTS.md` only, `CLAUDE.md` fallback, both user + project combined
- [ ] 5.3 Unit test that `CLAUDE.md` fallback emits `system.notice`
- [ ] 5.4 Unit test `TurnHandler` — system message at index 0 on first turn, not rebuilt on second turn
