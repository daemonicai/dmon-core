## Why

`SandboxContainmentChecker` (in `core/Dmon.Core/Permissions/`) decides whether a
write/edit/delete target under `sandbox` permission mode is contained within the
session's `assets/<session_id>/` subtree. It resolves the target's **real**
(symlink-followed) path and is written throughout to **fail closed** on a broken
or dangling symlink — its `ResolveExistingAncestor`/`ResolveRealPath` use
link-attribute detection so dangling links are caught on every platform and are
meant to return `null` (reject).

That intent is defeated on Linux. `FollowLinkToTarget` relies **solely** on
`catch (IOException)` to turn a broken chain into `null`, but
`File.ResolveLinkTarget(returnFinalTarget: true)` for a dangling symlink
**throws on macOS and returns the non-existent target on Linux** — the reverse
of what the method's own comment claims. This was proven empirically on Ubuntu CI
by the `tool-permission-hardening` change (commit `ac2e315`, PR #96), which fixed
the identical bug in the tools-layer `RealPathResolver` but explicitly left this
`Dmon.Core` twin out of scope. On Linux a dangling in-sandbox symlink therefore
resolves to a non-null, non-existent in-root path, the checker treats it as
contained, and the operation is **auto-allowed when it should be rejected** — a
fail-open in the sandbox containment boundary.

## What Changes

- Add an explicit existence guard to `SandboxContainmentChecker.FollowLinkToTarget`:
  after resolving `final`, if it is non-null but `Path.Exists(final)` is false,
  return `null` (fail closed) — so a resolved-but-non-existent target fails closed
  on **all** platforms, making Linux match macOS and the method's documented intent.
- Correct the backwards platform comment in `FollowLinkToTarget` (which claims
  Linux throws / macOS returns) to match the CI-verified reality.
- Add cross-platform tests that a dangling in-root symlink (as a leaf and as an
  ancestor) causes containment to **reject**, and that a live in-root symlink
  still resolves and is allowed (no regression). The real verification is Ubuntu
  CI, since macOS already fails closed via the throw.

## Capabilities

### Modified Capabilities

- `permission-model`: the sandbox asset-directory containment allowance gains an
  explicit requirement that its real-path resolution follows symlinks and **fails
  closed on a broken/dangling/unresolvable symlink on every platform**, closing
  the Linux fail-open. This mirrors the existing "Path-containment auto-allow
  resolves the real path" requirement that already binds the tools layer.

## Impact

- **Affected code:** `core/Dmon.Core/Permissions/SandboxContainmentChecker.cs`
  (`FollowLinkToTarget` — one guard clause + a comment correction).
- **Affected tests:** the `Dmon.Core.Tests` sandbox-containment suite.
- **Affected specs:** `permission-model` (1 ADDED requirement).
- **ADR:** ADR-006 (conservative permission model) — this hardens existing
  documented fail-closed behaviour; no ADR conflict.
- **Platform note:** behaviour only changes on Linux (macOS/Windows already fail
  closed via the throw); local macOS `make test` gates cannot observe the fix —
  Ubuntu CI is the real verification.
- Last outstanding item from the `repo-audit-2026-07-06` follow-up batch.
