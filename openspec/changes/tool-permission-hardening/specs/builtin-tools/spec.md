## ADDED Requirements

### Requirement: read_file auto-allow resolves symlinks before containment

`ReadFileTool.Evaluate` SHALL decide its implicit "within current working directory" `Allow` against the **real, symlink-resolved** path of the requested `path`, not the merely lexical `Path.GetFullPath` result. A path whose real (symlink-followed) target lies outside the current working directory SHALL NOT be auto-allowed; it SHALL return `PermissionResult.Prompt`. Resolution SHALL follow the same fail-closed, deepest-existing-ancestor-then-non-existent-tail approach as the engine's sandbox containment checker (`ResolveRealPath`): a path that cannot be resolved because it traverses a broken or unresolvable symlink SHALL fail closed to `PermissionResult.Prompt`, never `Allow`. Because `Dmon.Tools.Builtin` carries no dependency on `Dmon.Core`, this logic SHALL be reproduced within the package rather than referencing the engine's internal checker.

#### Scenario: Symlink inside CWD pointing outside is not auto-allowed

- **WHEN** `read_file` is called with a `path` that is (or traverses) a symlink living inside the current working directory whose real target resolves outside the current working directory
- **THEN** `ReadFileTool.Evaluate` returns `PermissionResult.Prompt`, not `PermissionResult.Allow`

#### Scenario: Regular file within CWD is still auto-allowed

- **WHEN** `read_file` is called with a `path` whose real, symlink-resolved target is within the current working directory
- **THEN** `ReadFileTool.Evaluate` returns `PermissionResult.Allow`

#### Scenario: Unresolvable symlink fails closed

- **WHEN** `read_file` is called with a `path` that traverses a broken or otherwise unresolvable symlink
- **THEN** `ReadFileTool.Evaluate` returns `PermissionResult.Prompt` (fail closed) and never `PermissionResult.Allow`

### Requirement: fetch refuses private-network and loopback addresses

`fetch` SHALL, after DNS resolution of the target host, refuse to issue the HTTP request when a resolved IP address falls in a loopback, link-local, private, or unique-local range, unless the host is explicitly opted in via the HTTP allowlist (`project.Settings.Http.Allow`). The refused ranges SHALL include IPv4 loopback `127.0.0.0/8`, IPv4 link-local `169.254.0.0/16` (including the cloud-metadata address `169.254.169.254`), the RFC1918 private ranges `10.0.0.0/8`, `172.16.0.0/12`, and `192.168.0.0/16`, IPv6 loopback `::1`, IPv6 link-local `fe80::/10`, and IPv6 unique-local `fc00::/7`; IPv4-mapped IPv6 addresses SHALL be classified by their embedded IPv4 form. If the host resolves to multiple addresses, refusal SHALL apply when **any** resolved address is in a refused range. Enforcement SHALL occur at **execute-time** on the request path (before the HTTP GET), returning an error string beginning with `"Error:"` and never issuing the request, because DNS may rebind between permission evaluation and use. In addition, `FetchTool.Evaluate` SHALL NOT return `PermissionResult.Allow` for a URL whose host is a literal IP address in a refused range unless that exact host string is present in the HTTP allowlist.

#### Scenario: URL resolving to loopback is refused at execute-time

- **WHEN** the LLM calls `fetch` with a URL whose host resolves to a loopback address (e.g. `127.0.0.1` or `::1`) and the host is not on the HTTP allowlist
- **THEN** the tool returns an error string beginning with `"Error:"`, no HTTP GET is issued, and no exception is thrown

#### Scenario: Cloud-metadata address is refused

- **WHEN** the LLM calls `fetch` with a URL whose host resolves to `169.254.169.254` and the host is not on the HTTP allowlist
- **THEN** the tool returns an error string beginning with `"Error:"` and no HTTP GET is issued

#### Scenario: RFC1918 private address is refused

- **WHEN** the LLM calls `fetch` with a URL whose host resolves to an address in `10.0.0.0/8`, `172.16.0.0/12`, or `192.168.0.0/16` and the host is not on the HTTP allowlist
- **THEN** the tool returns an error string beginning with `"Error:"` and no HTTP GET is issued

#### Scenario: Allowlisted private host is permitted

- **WHEN** the LLM calls `fetch` with a URL whose host resolves to a private or loopback address AND that host string is present in the HTTP allowlist
- **THEN** the SSRF guard does not refuse the request and the fetch proceeds

#### Scenario: Public host is still fetched normally

- **WHEN** the LLM calls `fetch` with a URL whose host resolves only to public (non-refused) addresses
- **THEN** the SSRF guard does not refuse and the tool returns the response body as before

#### Scenario: Evaluate does not auto-allow a literal private IP host

- **WHEN** `FetchTool.Evaluate` receives a URL whose host is a literal IP address in a refused range that is not present in the HTTP allowlist
- **THEN** it returns `PermissionResult.Prompt`, not `PermissionResult.Allow`
