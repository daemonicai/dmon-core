## ADDED Requirements

### Requirement: Path-containment auto-allow resolves the real path

Any tool `Evaluate` that grants an implicit `PermissionResult.Allow` on the basis that a target filesystem path is contained within the current working directory (or another trusted root) SHALL perform that containment check against the **real, symlink-resolved** path, not the merely lexical `Path.GetFullPath` result (which collapses `..` but does not follow symlinks). A path whose real target escapes the trusted root through a symlink SHALL NOT be auto-allowed, and a path that cannot be resolved (broken or unresolvable symlink) SHALL fail closed to `PermissionResult.Prompt`. This upholds ADR-006's conservative model: the implicit read-within-CWD allowance SHALL NOT be reachable through a symlink that points outside the current working directory.

#### Scenario: Symlink escape is not auto-allowed

- **WHEN** a tool `Evaluate` considers an implicit within-root `Allow` for a path whose real, symlink-resolved target lies outside the trusted root
- **THEN** it does not return `PermissionResult.Allow`; it returns `PermissionResult.Prompt`

#### Scenario: Genuine in-root path is auto-allowed

- **WHEN** a tool `Evaluate` considers an implicit within-root `Allow` for a path whose real, symlink-resolved target is within the trusted root
- **THEN** it may return `PermissionResult.Allow`

#### Scenario: Unresolvable path fails closed

- **WHEN** a tool `Evaluate` considers an implicit within-root `Allow` for a path that traverses a broken or unresolvable symlink
- **THEN** it returns `PermissionResult.Prompt` (fail closed) and never `PermissionResult.Allow`
