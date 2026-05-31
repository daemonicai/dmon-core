## ADDED Requirements

### Requirement: Permission mode is selected per session from the active profile
The permission gate SHALL operate under a permission mode supplied by the active agent profile (the `agent-profiles` capability), one of `coding` or `sandbox`, resolved once per session. The `coding` mode SHALL preserve the existing permission behaviour unchanged. The mode SHALL be available to tool permission evaluators so that write/edit/delete evaluation can account for it.

#### Scenario: Coding mode preserves existing behaviour
- **WHEN** a session runs under permission mode `coding`
- **THEN** read, write, edit, delete, bash, and http evaluation behave exactly as before this change, with no implicit write allowance anywhere

#### Scenario: Mode is fixed for the session
- **WHEN** a session has resolved its permission mode at start
- **THEN** that mode applies to every tool call for the session's lifetime

### Requirement: Sandbox mode grants implicit write to the session asset directory
Under permission mode `sandbox`, write, edit, and delete operations whose normalised target path is within the session's own `assets/<session_id>/` subtree SHALL be implicitly allowed without a prompt (risk `none`), mirroring the implicit-read-within-CWD allowance. Operations outside that subtree SHALL be evaluated exactly as in `coding` mode. The hardcoded denylist SHALL continue to be checked before any allowance and SHALL NOT be overridable by `sandbox` mode.

#### Scenario: Write within the asset directory is implicitly allowed
- **WHEN** a session under `sandbox` mode writes a file whose normalised path is within `assets/<session_id>/`
- **THEN** the write is allowed without a prompt

#### Scenario: Write outside the asset directory still prompts
- **WHEN** a session under `sandbox` mode writes a file whose normalised path is outside `assets/<session_id>/` and not otherwise approved
- **THEN** the write is evaluated as in `coding` mode and a prompt is issued

#### Scenario: Denylist still applies under sandbox
- **WHEN** a session under `sandbox` mode issues an operation matching a denylist entry, even within the asset directory
- **THEN** the operation is denied unconditionally and the `sandbox` allowance does not override the denylist
