## ADDED Requirements

### Requirement: ProviderRegistry tracks committed active model ID
`ProviderRegistry` SHALL maintain a `_activeModelId` field that is updated atomically inside `CommitPendingSwitch()` whenever a pending model ID is applied. `IProviderRegistry` SHALL expose this value via `string? GetCurrentModelId()`.

#### Scenario: GetCurrentModelId returns null before first commit
- **WHEN** `GetCurrentModelId()` is called before any `CommitPendingSwitch()` has applied a model change
- **THEN** it returns null

#### Scenario: GetCurrentModelId reflects committed model
- **WHEN** `SetModel("claude-haiku-4-5-20251001")` is called and `CommitPendingSwitch()` is called
- **THEN** `GetCurrentModelId()` returns `"claude-haiku-4-5-20251001"`

#### Scenario: GetCurrentModelId survives subsequent provider-only switch
- **WHEN** `SetProvider("openai")` is called (no `SetModel`) and `CommitPendingSwitch()` is called
- **THEN** `GetCurrentModelId()` retains the previously committed model ID (the provider switch alone does not clear it)

#### Scenario: GetCurrentModelId updates after model switch on different provider
- **WHEN** `SetProvider("openai")` and `SetModel("gpt-4o")` are both called before `CommitPendingSwitch()`
- **THEN** after commit, `GetCurrentModelId()` returns `"gpt-4o"`
