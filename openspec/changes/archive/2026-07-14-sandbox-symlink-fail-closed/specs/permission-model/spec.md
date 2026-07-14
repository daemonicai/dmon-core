## ADDED Requirements

### Requirement: Sandbox asset-containment resolves the real path and fails closed on unresolvable symlinks

The containment check that grants the `sandbox`-mode implicit write/edit/delete
allowance for a target within the session's `assets/<session_id>/` subtree SHALL
compute the target's **real, symlink-resolved** path — following any symlink in
the leaf or an ancestor to its final target — before deciding containment, not the
merely lexical `Path.GetFullPath` result. A target whose real, symlink-resolved
path escapes the `assets/<session_id>/` subtree SHALL NOT receive the implicit
allowance. A target that traverses a **broken, dangling, or otherwise
unresolvable** symlink SHALL be treated as **not contained** (fail closed) and
SHALL NOT receive the implicit allowance, and this fail-closed behaviour SHALL
hold on **every platform** — it SHALL NOT depend on a particular operating
system's choice to throw versus return a non-existent target when resolving a
dangling link. This mirrors, for the sandbox asset directory, the real-path
fail-closed guarantee already required of the tools-layer path-containment
auto-allow, upholding ADR-006's conservative model.

#### Scenario: Dangling symlink leaf is not contained

- **WHEN** the sandbox containment check evaluates a target whose leaf is a symlink
  pointing at a non-existent path (a dangling link)
- **THEN** the target is treated as not contained (fail closed) on every platform
  and the `sandbox` implicit allowance is not granted

#### Scenario: Dangling symlinked ancestor is not contained

- **WHEN** the sandbox containment check evaluates a target reached through an
  ancestor directory component that is a symlink pointing at a non-existent path
- **THEN** the target is treated as not contained (fail closed) on every platform
  and the `sandbox` implicit allowance is not granted

#### Scenario: Live in-sandbox symlink still resolves and is contained

- **WHEN** the sandbox containment check evaluates a target whose real,
  symlink-resolved path (through a live, resolvable symlink) lies within the
  session's `assets/<session_id>/` subtree
- **THEN** the target is treated as contained and remains eligible for the
  `sandbox` implicit allowance, unchanged by this hardening

#### Scenario: Live symlink escaping the sandbox is not contained

- **WHEN** the sandbox containment check evaluates a target whose real,
  symlink-resolved target lies outside the `assets/<session_id>/` subtree
- **THEN** the target is treated as not contained and the `sandbox` implicit
  allowance is not granted
