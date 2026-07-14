# Design: tool-permission-hardening

## Context

`Dmon.Tools.Builtin` ships the six builtin tools as a granular, `Dmon.Core`-free package (ADR-023 D6). Two of its permission checks are weaker than the engine's own filesystem containment logic:

- `ReadFileTool.Evaluate` (`tools/Dmon.Tools.Builtin/Tools/ReadFileTool.cs:47-49`) auto-allows a read when `Path.GetFullPath(path)` is under `Environment.CurrentDirectory`. `Path.GetFullPath` does not follow symlinks, so a symlink inside the CWD pointing outside it is auto-allowed.
- `FetchTool` (`tools/Dmon.Tools.Builtin/Tools/FetchTool.cs`) allows on an exact host-string allowlist and otherwise prompts, then `ExecuteAsync` (`:31`) does `GetAsync(url)` with no private-IP block.

The engine already has the correct pattern for the first problem: `core/Dmon.Core/Permissions/SandboxContainmentChecker.cs` `ResolveRealPath` resolves the deepest existing ancestor through symlinks, re-appends a non-existent tail, follows a leaf symlink, and **fails closed** (returns `null`) on a broken/unresolvable link. That type is `internal` to `Dmon.Core` and cannot be referenced from the `Dmon.Core`-free tools package.

## Goals

- `read_file`'s implicit within-CWD `Allow` is decided against the symlink-resolved real path; symlink escapes and unresolvable paths fail closed to `Prompt`.
- `fetch` refuses requests to loopback / link-local / private / unique-local addresses at execute-time, with an opt-in via the existing HTTP allowlist.
- Keep `Dmon.Tools.Builtin` free of any `Dmon.Core` dependency.
- Keep the guards proportionate to the low severity — no new configuration surface, no new protocol/ADR.

## Non-Goals

- No change to the permission gate architecture, RPC surface, or persisted formats.
- No new configuration keys — the SSRF opt-in reuses `Http.Allow`.
- No hardening of the other builtin tools (`write_file`, `edit_file`, `glob`, `bash`) — out of scope for #15/#16.
- No egress proxy, no per-request redirect-chain re-validation beyond the initial resolution (redirects are followed by the shared `HttpClient`; documented as a residual risk below, not addressed here — proportionate to severity).

## Key decisions

### 1. Symlink-resolution reuse (not reference)

`Dmon.Tools.Builtin` MUST NOT reference `Dmon.Core`, and `SandboxContainmentChecker` is `internal` to the engine. The symlink-safe containment logic is therefore **reproduced** inside the tools package (a small private helper mirroring `ResolveRealPath`'s behaviour: resolve the deepest existing ancestor through symlinks, re-append the non-existent tail, follow a leaf symlink, fail closed on broken/unresolvable links). This is deliberate duplication of a security primitive across a package boundary the ADRs mandate; the alternative — promoting the checker to a shared package — is larger than warranted for a low-severity fix and is out of scope. The behaviour, not the code, is what the spec pins.

### 2. read_file fails closed

If the real path cannot be resolved (broken/dangling/unresolvable symlink), `Evaluate` returns `PermissionResult.Prompt`, never `Allow`. This matches the engine checker's fail-closed stance and ADR-006's conservatism: an ambiguous path is never silently auto-allowed. `read_file` remains fully usable — a denied auto-allow becomes a prompt, not a hard failure, and `ExecuteAsync` still reads whatever the user approves.

### 3. SSRF refused ranges

After DNS resolution of the target host, refuse when the resolved address is in any of:

| Family | Range | Notes |
|--------|-------|-------|
| IPv4 loopback | `127.0.0.0/8` | includes `127.0.0.1` |
| IPv4 link-local | `169.254.0.0/16` | **includes cloud-metadata `169.254.169.254`** |
| IPv4 private | `10.0.0.0/8` | RFC1918 |
| IPv4 private | `172.16.0.0/12` | RFC1918 |
| IPv4 private | `192.168.0.0/16` | RFC1918 |
| IPv6 loopback | `::1/128` | |
| IPv6 link-local | `fe80::/10` | |
| IPv6 unique-local | `fc00::/7` | RFC4193 |

IPv4-mapped IPv6 addresses (`::ffff:a.b.c.d`) are mapped to their IPv4 form before range classification so a mapped private address cannot slip through. If a host resolves to multiple addresses, **any** address in a refused range causes refusal (an attacker cannot mix one public and one private A-record to pass).

### 4. Enforcement lives at execute-time (with a defensive Evaluate tightening)

The authoritative guard is at **execute-time** in `ExecuteAsync`, before `GetAsync`: resolve the host, classify, and return an `"Error:"` string (never issue the GET) if refused. This is the safe enforcement point because DNS can rebind between the permission evaluation and the actual request — a permission-time-only check is bypassable by TOCTOU. This upholds the tool's existing contract that failures are returned as `"Error:"` strings, not thrown.

Additionally, `FetchTool.Evaluate` is tightened so it does not return `Allow` for a URL whose host is a **literal** IP address in a refused range unless that exact host string is on the `Http.Allow` allowlist. The existing Evaluate already prompts for anything not exactly allowlisted, so this is a narrow belt-and-braces addition, not a behaviour reversal.

### 5. Opt-in via the existing allowlist

A user who genuinely needs `fetch` to reach a private/loopback host (e.g. a home-lab service) can add that host to `Http.Allow`. An allowlisted host is exempt from the SSRF refusal. No new config key is introduced; the opt-in is the mechanism users already have.

## Risks

- **Duplicated security primitive (decision 1).** The symlink-resolution logic now exists in two places (engine + tools package). If a bug is found in one, both must be fixed. Mitigated by pinning the *behaviour* in the spec and by unit tests in the tools package that assert the same fail-closed properties the engine checker asserts.
- **Redirect chains.** The shared `HttpClient` follows redirects; a public URL that 302-redirects to a private address is only guarded to the extent the handler re-resolves — full redirect-chain re-validation is out of scope for this low-severity fix and noted as residual.
- **False positives.** Legitimate fetches to LAN hosts now error unless allowlisted. Acceptable and documented; the opt-in path is explicit.
- **DNS resolution cost / failure.** `ExecuteAsync` now performs a resolution; a resolution failure is returned as an `"Error:"` string (the tool already returns `"Error:"` on unreachable hosts), so no new throw path is introduced.

## ADR check

ADR-006 (conservative permission model; "read within CWD is implicit; all writes require a prompt") is **binding**. Both changes strengthen it — the symlink fix ensures the implicit read-within-CWD allowance cannot be reached through a symlink escaping CWD, and the SSRF guard adds a deny that did not previously exist. Neither contradicts ADR-006 or any other binding ADR. **No BLOCKER; no superseding ADR required.**
