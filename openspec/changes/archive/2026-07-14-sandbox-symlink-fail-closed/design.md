## Context

`core/Dmon.Core/Permissions/SandboxContainmentChecker.cs` computes the real
(symlink-resolved) path of a candidate target so the caller can decide whether it
lies within the session's `assets/<session_id>/` sandbox subtree. The file is
deliberately written to **fail closed** on unresolvable symlinks:

- `IsSymlink` uses link-attribute detection (`FileInfo/DirectoryInfo.LinkTarget`),
  which is non-null for both live and dangling symlinks on every platform — so a
  dangling link is *detected* rather than skipped.
- `ResolveExistingAncestor` and `ResolveRealPath` call `FollowLinkToTarget` for any
  detected symlink and treat a `null` return as "reject" (fail closed).

The defect is isolated to `FollowLinkToTarget` (lines ~216–243). It calls
`File.ResolveLinkTarget(returnFinalTarget: true)` / `Directory.ResolveLinkTarget`
and depends **only** on `catch (IOException)` to produce `null` for a broken chain.
Per the CI-verified evidence from `tool-permission-hardening` (commit `ac2e315`,
PR #96):

- **macOS/Windows:** `ResolveLinkTarget` **throws `IOException`** for a dangling
  symlink → the catch fires → `null` → fail closed. Correct.
- **Linux:** `ResolveLinkTarget` **returns the non-existent target** without
  throwing → the catch never fires → a non-null, non-existent path is returned →
  the caller treats it as a resolved real path.

The method's comment states the divergence backwards (claims Linux throws, macOS
returns), which is how the bug survived review. This is the exact `Dmon.Core` twin
of the tools-layer `RealPathResolver.FollowLinkToTarget` bug already fixed by
`ac2e315`; that change was scoped to `Dmon.Tools.Builtin` and named this file as
out-of-scope follow-up.

## Goals / Non-Goals

**Goals:**
- Make `FollowLinkToTarget` fail closed on a dangling/unresolvable symlink on
  **all** platforms, so Linux matches macOS and the file's documented intent.
- Correct the backwards platform comment so the invariant is not re-broken.
- Lock the behaviour with tests that fail on Linux CI without the fix.
- Add a `permission-model` requirement asserting the sandbox containment's
  cross-platform fail-closed-on-broken-symlink property.

**Non-Goals:**
- No change to the tools-layer `RealPathResolver` (already fixed in `ac2e315`).
- No change to `ResolveRealPath` / `ResolveExistingAncestor` / `IsSymlink` control
  flow — they already fail closed correctly once `FollowLinkToTarget` returns
  `null`.
- No change to sandbox-mode semantics, the denylist, or the containment caller.
- No new symlink policy (e.g. allowing dangling links) — the intent stays "reject".

## Decisions

**D1 — Existence guard mirrors `ac2e315`.** After `FollowLinkToTarget` computes
and re-anchors `final`, add: if `final is not null && !Path.Exists(final)` →
return `null`. `Path.Exists` returns `false` for a non-existent target on every
platform, so a dangling link that resolved without throwing (Linux) now fails
closed identically to the throwing platforms. This is the same guard the tools
layer adopted, keeping the two twins consistent.

- Placement: the guard goes **after** the relative-path re-anchoring block, so it
  tests the fully-resolved absolute target. A relative non-existent target must be
  anchored before the existence test or it would be checked against the wrong CWD.
- `Path.Exists` follows symlinks, but `final` is already the *final* target of the
  chain (`returnFinalTarget: true`), so there is no further link to follow — a
  `true` result means the real target genuinely exists.

**D2 — Correct the comment.** Replace the backwards "Linux throws / macOS returns"
lines with the CI-verified description ("macOS/Windows throw for a dangling link;
Linux returns the non-existent target — the explicit `Path.Exists` guard below
fails both closed"). The comment is load-bearing: it explains why the `catch`
alone is insufficient.

**D3 — Spec: ADD to `permission-model`.** The existing "Sandbox mode grants
implicit write to the session asset directory" requirement does not mention
symlink resolution. Rather than overload it, ADD a sibling requirement — modelled
on the existing "Path-containment auto-allow resolves the real path" requirement —
stating that the sandbox asset-directory containment resolves the real path and
fails closed (does not treat as contained) on a broken/dangling/unresolvable
symlink on every platform.

## Risks / Trade-offs

- **macOS gates cannot observe the fix.** On macOS the throw already yields the
  correct result, so `make test` passes with or without the guard. The tests still
  encode the intent and are the real gate on **Ubuntu CI** (per the batch's
  standing lesson that CI is authoritative for symlink/filesystem behaviour). The
  task list calls this out so the reviewer/orchestrator do not read a green local
  run as proof.
- **Test construction.** A dangling symlink must be created pointing at a target
  path that does not exist. The test must place the link inside the sandbox root
  and assert containment *rejects* (returns `null` / not-contained), and separately
  that a **live** in-root symlink still resolves and is contained — guarding
  against an over-broad guard that would also reject valid links.
- **Blast radius is minimal:** one guard clause in one private method; all callers
  already handle `null` as fail-closed, so no caller changes and no behaviour
  change on platforms that already threw.
