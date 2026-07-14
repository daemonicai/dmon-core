# Proposal: tool-permission-hardening

## Why

Two builtin-tool permission checks are weaker than the sandbox path checker already shipped in the engine, and both are still present today:

- **read_file symlink escape (#15).** `ReadFileTool.Evaluate` grants an implicit `Allow` when `Path.GetFullPath(path)` is under the current working directory. `Path.GetFullPath` collapses `..` but does **not** resolve symlinks, so a symlink that lives inside the CWD yet points outside it (e.g. `./link → /etc`) is auto-allowed and read without a prompt. The engine already solves exactly this in `SandboxContainmentChecker.ResolveRealPath` (symlink-following, fail-closed), but `read_file` does not use that approach.
- **fetch SSRF gap (#16).** `FetchTool` issues `HttpClient.GetAsync(url)` after only a host-string allowlist check, with no block on hosts that resolve to loopback, link-local (including the `169.254.169.254` cloud-metadata address), or RFC1918/unique-local private ranges. An agent-driven `fetch` behind a permission prompt can therefore be steered at internal network resources.

Both are hardening gaps consistent with ADR-006's conservative model, not new behaviour. Severity is low (fetch is agent-driven behind a prompt; read_file's auto-allow only skips the prompt, it does not widen what the process could otherwise read), so the guards are kept proportionate.

## What Changes

- `read_file`'s implicit within-CWD `Allow` is decided against the **real, symlink-resolved** path (reusing the `SandboxContainmentChecker.ResolveRealPath` approach). A path whose real target escapes the CWD, or an unresolvable/broken symlink, no longer auto-allows — it returns `Prompt` (fail-closed).
- `fetch` gains an **SSRF guard**: after DNS resolution of the target host, a request whose resolved IP falls in a loopback / link-local / private / unique-local range is refused at **execute-time** with an `"Error:"` string and no HTTP GET is issued (DNS can rebind between permission-time and use, so the request path is the safe enforcement point). Hosts explicitly opted in via the HTTP allowlist are exempt. `FetchTool.Evaluate` additionally does not auto-`Allow` a literal private/loopback IP host that is not on the allowlist.
- No engine, RPC, protocol, persistence, or ADR change. `Dmon.Tools.Builtin` keeps its `Dmon.Core`-free dependency graph — the symlink-resolution logic is reproduced within the package (it must not reference the engine's internal `SandboxContainmentChecker`).

## Capabilities

### New Capabilities

None — no new capability is introduced.

### Modified Capabilities

- `builtin-tools` — adds hardening requirements for `read_file`'s containment auto-allow and `fetch`'s SSRF guard.
- `permission-model` — adds the general requirement that a path-containment auto-allow is decided against the real, symlink-resolved path.

## Impact

- **Code:** `tools/Dmon.Tools.Builtin/Tools/ReadFileTool.cs` (symlink-safe containment in `Evaluate`), `tools/Dmon.Tools.Builtin/Tools/FetchTool.cs` (execute-time SSRF refusal + Evaluate no-auto-allow of literal private IP hosts), plus a small symlink-resolution helper reproduced inside the package.
- **Tests:** new unit tests in `Dmon.Tools.Builtin` covering the symlink-escape, fail-closed, in-CWD-allow, SSRF-refusal (loopback / metadata / RFC1918 / IPv6), allowlist-opt-in, and public-host-still-fetched paths.
- **No** contract, wire, persisted-format, or configuration-schema change; the existing `Http.Allow` allowlist is reused as the opt-in.
- **Behaviour change users may notice:** a previously auto-allowed read through an in-CWD symlink now prompts; a `fetch` to a private/loopback address now returns an error unless allowlisted.
