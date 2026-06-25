# ADR-033: Rename the Gateway Host to `Dmon.Network` / `ndmon`

**Date:** 2026-06-25
**Status:** Accepted
**Amends:** ADR-012 (host name in Decisions 2/12 — terminology only), ADR-017 (host-name prose — terminology only), ADR-018 (host-name prose — terminology only), ADR-028 (frontends host list `Terminal, Gateway, Desktop` → `Terminal, Network, Desktop` — terminology only)
**Builds on:** ADR-024 (app-artifact independent versioning)

## Context

`frontends/Dmon.Gateway` is the WebSocket remote-access host (ADR-012, `remote-session-gateway` spec): connection-decoupled resumable sessions, Tailscale-fronted authentication, per-device keys (ADR-018), and agent-bound `create`/`attach` control frames.

The name "Gateway" has two problems in practice:

1. **Tooling mis-match.** The project was a plain console `Exe`, not a `PackAsTool` dotnet tool. dmonium used `~/.dotnet/tools/Dmon.Gateway` as its default binary candidate — a path that is never produced by `dotnet tool install` because `Dmon.Gateway` was never packaged as a tool. The health row was red out of the box until a user hand-built a binary and set `DMON_GATEWAY_PATH`.

2. **Semantic ambiguity.** "Gateway" reads as general infrastructure (API gateway, load balancer) rather than as the personal-assistant network host it is. The host is a singly-deployed, Tailscale-fronted, home-server process that owns your sessions and extends the agent across the network — a _network host_, not a gateway in the infrastructure sense.

The `gateway-packaging` change addresses both: it packages the host as a dotnet tool and renames it throughout. This ADR records the naming decision.

## Decision

1. **The project `frontends/Dmon.Gateway` is renamed `frontends/Dmon.Network`.** The dotnet-tool command is **`ndmon`**, installed to **`~/.dotnet/tools/ndmon`** via `dotnet tool install -g Dmon.Network`. dmonium's default candidate becomes exactly that path; `DMON_GATEWAY_PATH` is renamed `DMON_NETWORK_PATH` (clean break — no back-compat alias, per the project's "no production deployments / prefer clean breaks" policy).

2. **The host is an independently-versioned app artifact, exempt from the protocol-keyed `Major.Minor` lockstep** (ADR-024). It is not a `Dmon.*` first-party protocol package and does not join the NuGet release train. Versions are independent in the app-artifact release family, matching ADR-024's existing carve-out for app artifacts.

3. **"Gateway" terminology is dropped** across host code (`namespace Dmon.Network`, `Network*` types), dmonium (`NetworkManager`, "Network" health component, `DMON_NETWORK_PATH`), docs, and standing-spec prose (see ADR decision 4 below).

4. **Two naming boundaries are deliberately NOT crossed** by this rename decision:

   a. **Wire/contract strings.** The control-frame strings (`create`, `created`, `attach {sessionId, lastSeq}`, `session.createResult`, `session.loadResult`, `seq`/`headSeq`/`generation`), the `gw` discriminator, and the shared protocol namespace `Dmon.Protocol.Gateway` are part of the ADR-012/ADR-015 wire contract. They are **not renamed** by this ADR. Renaming them would be a wire-breaking change requiring a protocol-version bump per ADR-024.

   b. **Runtime config-section and on-disk strings.** `NetworkOptions.SectionName = "Gateway"`, `GetSection("Gateway")`, the `[dmon-gateway]` log prefix, and the `~/.dmon/gateway` device-key store path are runtime/on-disk artefacts whose rename requires a config-migration story. Their rename is **explicitly deferred** to a later change and is not part of this naming decision.

   These two boundary decisions are recorded here so a future reader can see exactly where the rename stops and why.

## Consequences

- **dmonium's network-host health row is green out of the box** after `dotnet tool install -g Dmon.Network`. The default candidate `~/.dotnet/tools/ndmon` is deterministically produced by the tool install (no casing ambiguity, unlike `Dmon.Gateway`).
- **A `make network` target** (analogous to `make build`) packages and installs `ndmon`.
- **No protocol version bump required.** The wire contract is unchanged; existing iOS clients continue to function without a client release.
- **Config migration is deferred.** Users with existing `DMON_GATEWAY_PATH` must update to `DMON_NETWORK_PATH`; the runtime `Gateway:` section name and `~/.dmon/gateway` store path are unchanged for now.
- **ADR-012/017/018/028 bodies remain correct** for their architectural substance; only the host name in their prose is superseded by this ADR's terminology.

## Alternatives

- **Keep the name "Gateway".** Rejected: the health-row default path `~/.dotnet/tools/Dmon.Gateway` is never produced by `dotnet tool install`. The fix (adding `PackAsTool`) does not require a rename, but the rename is the right moment to clear the semantic ambiguity and align the tool name with the `ndmon` command.
- **`gwmon` or `dmon-net` as the tool command.** Rejected: `ndmon` follows the `dmon` pattern (one letter prefix) and reads as "network dmon" unambiguously. `gwmon` carries the "gateway" word being dropped; `dmon-net` would clash with potential NuGet package naming.
- **Rename the wire strings.** Rejected: a wire-breaking change requires a protocol-version bump (ADR-024 `Major.Minor`), a simultaneous iOS client release, and a migration path. The terminology gain does not justify the coordination cost.
- **Write a superseding ADR for ADR-012/017/018/028.** Rejected: no numbered decision in any of those ADRs is reversed. "Gateway" was the host's _name_, used descriptively; no numbered decision is load-bearing on the name. A terminology amendment note is the correct mechanism.

## Open Questions

None material. The two deliberate non-renames (wire strings and runtime config section) are recorded in Decision 4 and are deferred to a later change.

## Relationship to other ADRs

- **ADR-012** — host name in Decisions 2 and 12 ("the `Dmon.Gateway` host", "the gateway") → "the `Dmon.Network` host" / "the network host". All architectural decisions (transport, session decoupling, Tailscale boundary, single-tenancy, fencing, per-device keys, loopback bind) are unchanged.
- **ADR-017** — host-name prose ("the `Dmon.Gateway` host", "the remote gateway") → "the `Dmon.Network` host" / "the network host". The client-side TailscaleKit embedding decision is entirely unchanged.
- **ADR-018** — host-name prose ("the remote gateway", "`Dmon.Gateway`'s `SharedKeyAuthenticator`", "`GatewayOptions.SharedKey`") → "the network host". Note that `GatewayOptions` → `NetworkOptions` is a code rename already completed in Block 1 of the `gateway-packaging` change; this ADR is the decision record for that rename. All auth decisions (per-device revocable key set, hashing, fencing, hot reload) are unchanged.
- **ADR-028** — the `frontends/` host list in Decision 2 reads "`Terminal`, `Gateway`, `Desktop`"; "Gateway" → "Network" (host name only). Bucket placement, the daemon/services topology, and the `dcal` rename all stand.
- **ADR-024** — this rename relies on ADR-024's independent versioning for app artifacts. `Dmon.Network` is not on the protocol-lockstep NuGet train; it may version independently within the app-artifact release family.
