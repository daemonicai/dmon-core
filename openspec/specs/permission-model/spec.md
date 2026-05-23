## ADDED Requirements

### Requirement: Read permission — CWD subtree implicit
The system SHALL allow read operations on any path within the CWD subtree at invocation time without prompting the user.

#### Scenario: Read within CWD requires no confirmation
- **WHEN** a tool attempts to read a file within the CWD subtree
- **THEN** the permission gate allows the operation without emitting `tool.confirmRequest`

#### Scenario: Read outside CWD requires confirmation
- **WHEN** a tool attempts to read a file outside the CWD subtree
- **THEN** the permission gate emits `tool.confirmRequest` with `risk: low`

### Requirement: Tree-based path grants
Path grants SHALL be tree-based: approving a path grants implicit access to all paths beneath it. Paths SHALL be normalised (symlinks resolved, `..` collapsed) before grant evaluation.

#### Scenario: Grant covers subtree
- **WHEN** the user approves `/Users/rendle/foo` for read
- **THEN** subsequent read attempts on `/Users/rendle/foo/bar/baz.txt` are allowed without prompting

#### Scenario: Normalised path evaluated correctly
- **WHEN** a tool attempts to read `/Users/rendle/foo/../bar/file.txt`
- **THEN** the path is normalised to `/Users/rendle/bar/file.txt` before grant evaluation

### Requirement: Write, Edit, Delete — no implicit allows
The system SHALL require explicit user approval for all write, edit, and delete operations, including within the CWD subtree.

#### Scenario: Write within CWD requires confirmation
- **WHEN** a tool attempts to write a file within the CWD subtree
- **THEN** the permission gate emits `tool.confirmRequest` with `risk: high`

#### Scenario: Approved write path remembered at project scope
- **WHEN** the user approves a write to `/path/foo` with "Allow for project"
- **THEN** the grant is persisted to `.daemon/settings.yaml` and subsequent writes to the `/path/foo/` subtree are allowed without prompting in this and future sessions

### Requirement: Bash — simple command glob approval
Simple bash commands (no pipes, semicolons, `&&`, `||`, `$()`, backticks, or redirects) SHALL be matched against stored glob patterns. Approved patterns allow matching commands without prompting.

#### Scenario: Novel simple command prompts user
- **WHEN** a tool invokes a bash command that matches no stored approval pattern
- **THEN** the permission gate emits `tool.confirmRequest` with `risk: high`

#### Scenario: Stored pattern auto-approves matching command
- **WHEN** a tool invokes `git diff HEAD~1` and `git *` is in the stored approvals
- **THEN** the permission gate allows the command without prompting

#### Scenario: User shown pattern suggestion on approval
- **WHEN** the user approves a simple bash command
- **THEN** the host offers both the exact command string and a suggested broader pattern (e.g., `git *`) for the user to choose what to store

### Requirement: Bash — composite commands always prompt
Composite bash commands SHALL always emit `tool.confirmRequest` regardless of stored approvals. A command is composite if it contains any of: pipes (`|`, `|&`), command separators (`;`, `&&`, `||`, newlines, `&`), command substitution (`$()`, backticks), process substitution (`<()`, `>()`), redirects (`>`, `>>`, `<`, `<<`, `<<<`, `2>`, `&>`, `>&`), subshells/groups (`( ... )`, `{ ... ; }`), or inline environment assignments. Ambiguous parses SHALL be treated as composite.

#### Scenario: Heredoc treated as composite
- **WHEN** a tool invokes a command containing `<<` or `<<<`
- **THEN** the permission gate emits `tool.confirmRequest` with `risk: high` regardless of stored approvals

#### Scenario: Inline env assignment treated as composite
- **WHEN** a tool invokes `FOO=bar npm test` and `npm *` is approved
- **THEN** the permission gate still emits `tool.confirmRequest` with `risk: high`

#### Scenario: Composite command bypasses stored approvals
- **WHEN** a tool invokes `git log | grep fix` and both `git *` and `grep *` are approved
- **THEN** the permission gate still emits `tool.confirmRequest` with `risk: high`

#### Scenario: Composite commands cannot be permanently approved
- **WHEN** the user approves a composite command
- **THEN** only "Allow once" is offered; "Allow for project" and "Allow globally" are not available for composite commands

### Requirement: HTTP — per-domain, project-scoped
HTTP calls from tools SHALL require approval per domain. Domain approvals are stored at project level only and do not propagate to user-global settings.

#### Scenario: First HTTP call to a domain requires approval
- **WHEN** a tool makes an HTTP request to a domain not in `.daemon/settings.yaml`
- **THEN** the permission gate emits `tool.confirmRequest` with `risk: high` showing the domain

#### Scenario: Approved domain is not global
- **WHEN** `api.github.com` is approved in project A
- **THEN** a tool in project B attempting to call `api.github.com` still requires approval

### Requirement: Grant precedence — most-specific wins
Within a scope, the most-specific rule decides. For path-based tiers, the longest matching path prefix wins. For bash globs, an explicit `deny` pattern beats an `allow` pattern; ties resolve to deny.

#### Scenario: Deny subtree under an allowed tree
- **WHEN** `/Users/me/work` is allowed for read and `/Users/me/work/secrets` is denied
- **THEN** reads under `/Users/me/work/secrets/` are blocked while reads elsewhere under `/Users/me/work/` are allowed

#### Scenario: Bash deny pattern overrides allow
- **WHEN** `git *` is allowed and `git push --force*` is denied
- **THEN** `git push --force origin main` is rejected

### Requirement: Hardcoded denylist
The system SHALL maintain a hardcoded denylist of dangerous bash command patterns that cannot be approved at any permission level. The denylist SHALL cover at minimum: `rm` targeting root or system directories, disk format and wipe commands (`mkfs`, `dd if=/dev/zero`, `shred`), security-disable patterns (`chmod -R 777 /`, `chattr -i /`), fork bombs, and any invocation of `sudo` or `su`.

#### Scenario: Denylisted command rejected unconditionally
- **WHEN** a tool invokes a command matching a denylist pattern
- **THEN** the permission gate rejects the operation, does not emit `tool.confirmRequest`, and reports the rejection to the agent

#### Scenario: Denylist cannot be overridden by settings
- **WHEN** a user attempts to add a denylist pattern to the stored approvals
- **THEN** the system ignores the approval and continues to reject matching commands

### Requirement: Permission persistence levels
Every permission prompt SHALL offer four options: Allow once, Allow for project, Allow globally, Deny.

#### Scenario: Allow for project stored correctly
- **WHEN** the user selects "Allow for project" for a read path grant
- **THEN** the grant is written to `.daemon/settings.yaml` and applies to all future sessions in the project

#### Scenario: Allow globally stored correctly
- **WHEN** the user selects "Allow globally" for a bash pattern approval
- **THEN** the approval is written to `~/.daemon/settings.yaml` and applies across all projects

#### Scenario: Project settings take precedence over global
- **WHEN** a path is denied in `.daemon/settings.yaml` and approved in `~/.daemon/settings.yaml`
- **THEN** the project-level deny takes precedence
