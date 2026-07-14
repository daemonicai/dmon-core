# DEVLOG — tool-permission-hardening

## NEXT
- **Block 2 = tasks 2.1–2.5** (fetch SSRF guard). Pre-resolved design note: to thread the HTTP allowlist to the execute path (task 2.3), inject `IPermissionSettings` — it is a DI-registered singleton (`core/Dmon.Core/DaemonServiceExtensions.cs:104`), so `FetchTool` can constructor-inject it and read `.Settings.Http.Allow` live at execute-time; registration is at `AddBuiltinToolsExtensions.cs:30–34` (the extra ctor arg is DI-supplied). Enforce the SSRF refusal at **execute-time** (before `GetAsync`), with a defensive `Evaluate` tightening (do not auto-`Allow` a literal private-IP host unless allowlisted). Redirect-chain re-validation is explicitly OUT of scope (design residual note).
- Then Gates tasks 3.1–3.3 (whole-change) → done.

## Section 1 — read_file symlink-safe containment (#15) — DONE (tasks 1.1–1.3)

Made `ReadFileTool.Evaluate`'s implicit within-CWD auto-`Allow` symlink-safe and fail-closed.

- **New `tools/Dmon.Tools.Builtin/RealPathResolver.cs`** (`internal static`) reproduces the engine's `SandboxContainmentChecker` resolution *behaviour* (not a reference — ADR-023 forbids `Dmon.Tools.Builtin`→`Dmon.Core`). Four members mirrored member-for-member: `ResolveRealPath` (parent-first, then leaf-symlink follow), `ResolveExistingAncestor` (deepest-existing-ancestor walk + literal tail re-append), `IsSymlink` (via `FileInfo/DirectoryInfo.LinkTarget` — NOT `Path.Exists`, which fails *open* on dangling links on Linux/CI-Ubuntu), `FollowLinkToTarget` (`ResolveLinkTarget(returnFinalTarget:true)`, catch `IOException`→`null`).
- **`Evaluate`** now resolves BOTH the target and the CWD (so macOS `/tmp`→`/private/tmp` doesn't false-`Prompt`); `null` from either resolution → `Prompt`, never `Allow`. Existing `IsUnder` (boundary-aware) and `ExecuteAsync` unchanged.
- **Decision — fail closed:** unresolvable/broken/dangling link → `Prompt` (the auto-allow degrades to a prompt, not a hard error; the tool stays usable). Strengthens ADR-006's "read within CWD is implicit"; no ADR contradiction.
- **Tests (5 new, 107/107 in the tools project):** leaf symlink-out→Prompt, regular-in→Allow, broken-link→Prompt, relative-in→Allow, **symlinked-ancestor-dir escape→Prompt** (added post-review to cover the subtle `ResolveExistingAncestor` path). CWD-mutating tests live in `[Collection("CwdMutating")]`, restore CWD in `finally`.

**Reviewer:** Approve, no blockers; verified line-by-line parity with the engine checker. Non-blocking nit (ancestor-symlink test) was closed. Architectural notes surfaced for later, NOT fixed here: (1) residual TOCTOU between `Evaluate` and `ExecuteAsync` (inherent to evaluate-then-execute; `ExecuteAsync` out of this block's scope); (2) duplicated `ResolveRealPath` primitive can silently diverge (accepted per design D1; mitigated by shared spec + parallel tests + doc cross-ref).

**Gates:** `make build` 0-warn; full `env -u MEKO_API_KEY make test` all projects green; `openspec validate --strict` valid.
