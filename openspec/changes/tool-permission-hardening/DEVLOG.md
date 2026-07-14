# DEVLOG — tool-permission-hardening

## NEXT
- **Change COMPLETE — all tasks ticked (1.1–1.3, 2.1–2.5, 3.1–3.3).** Ready for PR + merge, then archive.

## Section 1 — read_file symlink-safe containment (#15) — DONE (tasks 1.1–1.3)

Made `ReadFileTool.Evaluate`'s implicit within-CWD auto-`Allow` symlink-safe and fail-closed.

- **New `tools/Dmon.Tools.Builtin/RealPathResolver.cs`** (`internal static`) reproduces the engine's `SandboxContainmentChecker` resolution *behaviour* (not a reference — ADR-023 forbids `Dmon.Tools.Builtin`→`Dmon.Core`). Four members mirrored member-for-member: `ResolveRealPath` (parent-first, then leaf-symlink follow), `ResolveExistingAncestor` (deepest-existing-ancestor walk + literal tail re-append), `IsSymlink` (via `FileInfo/DirectoryInfo.LinkTarget` — NOT `Path.Exists`, which fails *open* on dangling links on Linux/CI-Ubuntu), `FollowLinkToTarget` (`ResolveLinkTarget(returnFinalTarget:true)`, catch `IOException`→`null`).
- **`Evaluate`** now resolves BOTH the target and the CWD (so macOS `/tmp`→`/private/tmp` doesn't false-`Prompt`); `null` from either resolution → `Prompt`, never `Allow`. Existing `IsUnder` (boundary-aware) and `ExecuteAsync` unchanged.
- **Decision — fail closed:** unresolvable/broken/dangling link → `Prompt` (the auto-allow degrades to a prompt, not a hard error; the tool stays usable). Strengthens ADR-006's "read within CWD is implicit"; no ADR contradiction.
- **Tests (5 new, 107/107 in the tools project):** leaf symlink-out→Prompt, regular-in→Allow, broken-link→Prompt, relative-in→Allow, **symlinked-ancestor-dir escape→Prompt** (added post-review to cover the subtle `ResolveExistingAncestor` path). CWD-mutating tests live in `[Collection("CwdMutating")]`, restore CWD in `finally`.

**Reviewer:** Approve, no blockers; verified line-by-line parity with the engine checker. Non-blocking nit (ancestor-symlink test) was closed. Architectural notes surfaced for later, NOT fixed here: (1) residual TOCTOU between `Evaluate` and `ExecuteAsync` (inherent to evaluate-then-execute; `ExecuteAsync` out of this block's scope); (2) duplicated `ResolveRealPath` primitive can silently diverge (accepted per design D1; mitigated by shared spec + parallel tests + doc cross-ref).

**Gates:** `make build` 0-warn; full `env -u MEKO_API_KEY make test` all projects green; `openspec validate --strict` valid.

## Section 2 — fetch SSRF guard (#16) — DONE (tasks 2.1–2.5)

Added an execute-time SSRF guard to `fetch`.

- **New `tools/Dmon.Tools.Builtin/SsrfGuard.cs`** (`internal static`, BCL-only, no `Dmon.Core` dep per ADR-023 D6): `IsRefused(IPAddress)` + `AnyRefused(IEnumerable<IPAddress>)`. Refuses IPv4 `127/8`, **`0.0.0.0/8`**, `169.254/16` (incl `169.254.169.254`), `10/8`, `172.16–31/12`, `192.168/16`; IPv6 `::1`, **`::`**, `fe80::/10`, `fc00::/7`; IPv4-mapped IPv6 un-mapped first.
- **`FetchTool`**: constructor-injects `IPermissionSettings` (DI singleton) to read `Http.Allow` live; `CheckSsrfAsync` runs **before** `GetAsync` — allowlist exemption on `uri.Host`, literal IP via `uri.DnsSafeHost` skips DNS, else `Dns.GetHostAddressesAsync`; refusal AND resolution failure both return `"Error:"`, never throw. Explicit `http`/`https` **scheme gate**. `Evaluate` returns `Allow` only on exact allowlist match (no new literal-IP branch — that was the dead code removed in review).
- **Injectable resolver seam** (`Func<string,CancellationToken,Task<IPAddress[]>>`, internal test ctor + `InternalsVisibleTo`) so the hostname→resolve→AnyRefused path (the primary SSRF vector) is actually tested, not just literal-IP short-circuits.
- **Opt-in reuses `Http.Allow` — NO new config key.** Redirect-chain re-validation + the DNS-rebind window between guard-resolve and HttpClient-resolve are conscious residuals (design Non-Goals/Risks).

**Review (2 rounds):** first pass Request-changes — B1 `0.0.0.0/8` and B2 `::` were unrefused loopback bypasses (reach the local MLX runtime at :8800), B3 the `Evaluate` literal-IP block was dead code (both branches → `Prompt`). All fixed; N1 injectable-resolver + hostname tests, N2 no-throw, N3 `172/12` boundary, N5 scheme gate added. Orchestrator added `0.0.0.0`/`::` to the delta spec's refused set (spec "SHALL include" is a floor). Second pass **Approve**.

**Gates:** `make build` 0-warn; full `make test` 20/20 projects green (Builtin 130/130); `openspec validate --strict` valid.
