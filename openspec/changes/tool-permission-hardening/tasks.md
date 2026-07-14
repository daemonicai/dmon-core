# Tasks: tool-permission-hardening

## 1. read_file symlink-safe containment (#15)

- [x] 1.1 Add a private symlink-resolution helper inside `Dmon.Tools.Builtin` that mirrors `SandboxContainmentChecker.ResolveRealPath` behaviour (resolve deepest existing ancestor through symlinks, re-append non-existent tail, follow a leaf symlink, fail closed to `null` on a broken/unresolvable link). Do NOT reference `Dmon.Core`.
- [x] 1.2 Change `ReadFileTool.Evaluate` to resolve the real path of `path` and the CWD before the `IsUnder` containment check; auto-`Allow` only when the real target is within the CWD, and return `Prompt` when the real target escapes CWD or the path is unresolvable (fail closed).
- [x] 1.3 Add unit tests: (a) symlink inside CWD pointing outside → `Prompt`; (b) regular file within CWD → `Allow`; (c) broken/unresolvable symlink → `Prompt`; (d) plain in-CWD relative path unchanged → `Allow`.

## 2. fetch SSRF guard (#16)

- [ ] 2.1 Add an IP-range classifier inside `Dmon.Tools.Builtin` covering IPv4 loopback `127.0.0.0/8`, link-local `169.254.0.0/16` (incl. `169.254.169.254`), RFC1918 `10.0.0.0/8` / `172.16.0.0/12` / `192.168.0.0/16`, IPv6 `::1`, `fe80::/10`, `fc00::/7`, and IPv4-mapped IPv6 mapped to their embedded IPv4 form.
- [ ] 2.2 In `FetchTool.ExecuteAsync`, before `GetAsync`, resolve the target host's addresses; if the host is not on the HTTP allowlist and any resolved address is in a refused range, return an `"Error:"` string and do not issue the request. Handle resolution failure by returning an `"Error:"` string (no throw).
- [ ] 2.3 Thread the HTTP allowlist to the execute path so an allowlisted host is exempt from the SSRF refusal (reuse `Http.Allow`; no new config key).
- [ ] 2.4 Tighten `FetchTool.Evaluate` so it does not return `Allow` for a URL whose host is a literal IP address in a refused range unless that exact host string is on the allowlist.
- [ ] 2.5 Add unit tests: loopback refused (no GET); `169.254.169.254` refused; each RFC1918 range refused; IPv6 `::1`/`fe80::`/`fc00::` refused; allowlisted private host permitted; public host still fetched; `Evaluate` does not auto-allow a literal private IP host.

## 3. Gates

- [ ] 3.1 `make build` is clean (no warnings; `TreatWarningsAsErrors`).
- [ ] 3.2 `env -u MEKO_API_KEY make test` is green (new tests plus all existing tests).
- [ ] 3.3 `openspec validate tool-permission-hardening --strict` passes.
