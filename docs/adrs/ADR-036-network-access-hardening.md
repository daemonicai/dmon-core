# ADR-036: Network Access Hardening — Fail-Closed Auth on Non-Loopback Binds and a Browser-Origin Allowlist

**Date:** 2026-07-14
**Status:** Accepted
**Amends:** ADR-012 (Decision 12), ADR-018 (Decision 2)

## Context

`Dmon.Network` is the single-tenant WebSocket remote host (ADR-012, renamed from `Dmon.Gateway` per ADR-033). Its `/ws` HTTP-upgrade edge (`NetworkConnectionEndpoint.HandleAsync`) performs the device-key (Bearer) authentication ADR-018 defined: the presented `Authorization: Bearer <token>` is matched against the active device-key set, with an **empty set meaning "auth disabled"** (ADR-018 Decision 2 — "an operator cannot lock themselves out"). A separate startup-time bind policy (`NetworkBindPolicy.Validate`, from the loopback-by-default posture of ADR-012 Decision 12) allows a loopback bind unconditionally, always rejects wildcard/all-interfaces binds, and allows a *specific* non-loopback address only behind the `Network:AllowNonLoopbackBind` opt-in.

A security review of `main` found two access-control gaps at this edge, both consistent with the letter of ADR-012/ADR-018 but not with their intent:

1. **Empty-set auth fails *open* even on an exposed bind.** ADR-018 Decision 2's empty-set-disabled rule is unconditional in code: an absent/empty `devices.json` authorizes **every** `/ws` upgrade regardless of bind. Decision 2's rationale — "over the tailnet … the operator cannot lock themselves out" — is about the intended loopback + `tailscale serve` deployment, where reaching the socket already means "on the tailnet." But the `AllowNonLoopbackBind` escape hatch lets the host listen directly on a LAN/tailnet interface; on that bind, an empty store leaves the agent wide open to anything that can route to the address. The bind gate protects the *bind*, never *auth*.

2. **The WebSocket upgrade has no `Origin` check.** `HandleAsync` reads only the `Authorization` header before accepting the socket. Browsers do **not** apply the same-origin policy to WebSocket upgrades, so a malicious page loaded in any browser that can route to the host (e.g. over the tailnet) can open `/ws` and drive a session cross-site. Native clients (iOS `URLSessionWebSocketTask`, desktop `ClientWebSocket`) send **no** `Origin` header; only browsers do.

Both are "reject before you accept" gaps. The fix is to make the edge fail closed for the exposed cases while preserving the local-dev and native-client paths that legitimately carry no credential / no origin. Neither fix contradicts an accepted decision — each closes a case the original rationale never covered — so this is an **amending** ADR (following the ADR-018-amends-ADR-012 precedent), not a superseding one. It changes **behaviour and configuration only**: the ADR-003 wire contract (the `gw` discriminator, control frames, command/event shapes), session storage (ADR-004), provider auth (ADR-005), the permission model (ADR-006), and the non-empty device-key path, file-backed store, hot-reload, and revocation-fencing of ADR-018 (Decisions 1, 3–7) are all untouched.

## Decision

1. **Empty/absent device-key set fails closed on a non-loopback bind (amends ADR-018 Decision 2).** The empty-set-disabled rule is **narrowed to a loopback bind**. Concretely:
   - **Loopback bind + empty/absent set →** auth disabled; every upgrade authorized, exactly as ADR-018 Decision 2 specifies today. The "operator cannot lock themselves out on their own box" guarantee is preserved for the case it was written for.
   - **Non-loopback bind + empty/absent set →** the host **rejects every `/ws` upgrade with HTTP 401** before any socket is opened; it does **not** authorize via the empty-set short-circuit. "Non-loopback bind" means a specific non-loopback address admitted through the `Network:AllowNonLoopbackBind` opt-in — the predicate `NetworkBindPolicy.IsNonLoopbackWithOptIn(BindAddress, AllowNonLoopbackBind)`. Because wildcard/all-interfaces binds are already fatal at startup, the only two states that reach the edge are (i) loopback and (ii) non-loopback-opt-in, so "not loopback" is well-defined with no third case.
   - **Non-empty set (any bind) →** unchanged: per-device Bearer auth exactly as ADR-018 Decision 1 (match active entries by constant-time hash compare, tag the connection with the matched `keyId`, 401 on miss).

   This preserves Decision 2's actual guarantee (loopback/tailnet: no lockout) and only closes the non-loopback case the rationale ("over the tailnet") never covered. It is consistent with — indeed extends — ADR-018 **Decision 5**, which already mandates fail-closed ("never fail open to 'disabled'") for a malformed store: the same fail-closed posture now applies to an empty store on an exposed interface. The operator-facing consequence is that a host bound to a non-loopback address with no paired device rejects all upgrades until the first device is paired; the startup log must state this plainly (auth is **fail-closed until a device is paired**, not "disabled").

2. **A browser-`Origin` allowlist as defence-in-depth at the upgrade edge (amends ADR-012 Decision 12).** ADR-012 Decision 12 names an optional shared key (generalized to a device-key set by ADR-018) as the sole defence-in-depth atop the Tailscale boundary. This ADR **adds** a second, browser-specific control evaluated in `HandleAsync` **before** `AcceptWebSocketAsync`:
   - **No `Origin` header →** allowed to proceed to the device-key check. Native clients send no `Origin`; this path must stay open or every shipped client breaks.
   - **`Origin` header present →** rejected with **HTTP 403** before any socket is opened **unless** the origin exactly matches a configured allowlist entry (ordinal string comparison against `Network:AllowedOrigins`).
   - **Default allowlist is empty →** every `Origin`-bearing (browser) upgrade is rejected by default; browser access is opt-in.

   The `Origin` check is **defence-in-depth atop** the device-key authentication, not a replacement for it: **both** checks apply to every upgrade, and passing one never waives the other. It is deliberately **not** an auth control — `Origin` is trivially forgeable by a non-browser client — but it defends against the one attacker who *cannot* forge it: a real browser bound by the Origin-setting rules. HTTP 403 (not 401) is used for a rejected origin, because no credential was wrong; this distinguishes "your browser origin is not permitted" from "your token is missing/invalid" and keeps 401 reserved for the device-key path. The allowlist is a new `Network:AllowedOrigins` option (a string set, default empty) on the same `Network:*` configuration surface as the existing options; the host's on-disk config/state home remains `~/.dmon/network`.

3. **No wire-contract or transport change.** This ADR changes only the accept/reject behaviour and configuration at the `/ws` HTTP-upgrade edge. The ADR-003 wire shape (the `gw` discriminator, `attach`/`attached`/`ack`/`ping`/`pong`/`create`/`created`/`createRejected` control frames, command/event framing), the WebSocket transport and session-decoupling model (ADR-012 Decisions 1–9, 11), and ADR-018's store, hot-reload, and revocation-fencing (Decisions 1, 3–7) are all unchanged. No protocol version bump is required.

## Consequences

- **An exposed host without a paired device is now closed, not open.** The one configuration this changes — a non-loopback bind with an empty/absent `devices.json` — flips from authorizing everything to rejecting everything with 401. This is the intended fix; the startup log must make the fail-closed state and the remedy (pair a device) explicit. The common loopback + `tailscale serve` path is unchanged.
- **Browser access is opt-in.** With a default-empty allowlist, any browser-originated upgrade is rejected. No first-party browser client ships today (clients are iOS/desktop, origin-less), so nothing shipped breaks; a future browser frontend must add its exact `scheme://host[:port]` origin to `Network:AllowedOrigins`.
- **Defence-in-depth, not a new trust boundary.** Tailscale remains the authentication and encryption boundary (ADR-012); the device-key set remains the credential (ADR-018). The two additions harden the edge without adding a per-origin credential model, a CORS framework, or origin-scoped session partitioning.
- **Single-tenancy and the wire contract are intact.** No session-ownership matrix, no per-origin identity, no protocol change — the amendments live entirely at the HTTP-upgrade edge, and clients are unaffected on the loopback/native path.

## Alternatives

- **Leave empty-set-disabled unconditional (ADR-018 as written).** Rejected: it leaves an operator who opts into a non-loopback bind with no auth at all, contradicting ADR-018 Decision 5's fail-closed intent and ADR-012's zero-public-exposure posture.
- **Reject *all* non-loopback binds outright (remove the opt-in).** Rejected: the `AllowNonLoopbackBind` escape hatch is a deliberate operational affordance; the correct fix is to make auth fail closed on that bind, not to remove the bind option.
- **Treat a bad `Origin` as 401 and fold it into the auth result.** Rejected: an origin rejection is not an authentication failure; conflating it with 401 would let a browser silently retry with credentials and would blur the two independent gates. 403 keeps them distinct.
- **A full CORS / per-origin-credential model.** Rejected as over-scoped: the deployment is single-tenant behind Tailscale; an exact-match allowlist (default empty) is the minimal control that closes the cross-site-WebSocket gap.

## Relationship to other ADRs

- **ADR-012** — *Amends Decision 12.* Adds a browser-`Origin` allowlist as a second defence-in-depth control alongside the optional key at the same upgrade edge. The transport, session decoupling, control sub-protocol, single-tenant model, and "loopback + `tailscale serve`, never a public NIC" posture (Decisions 1–11) are unchanged.
- **ADR-018** — *Amends Decision 2; reinforces Decision 5.* Narrows the empty-set-disabled rule to a loopback bind; on a non-loopback bind an empty/absent set fails closed with 401, extending Decision 5's malformed-store fail-closed posture to the empty-set-on-exposed-bind case. Decisions 1, 3–7 (the non-empty device-key path, hashing, file-backed store, hot-reload, revocation-fencing, `sessionId`-is-not-a-credential) are untouched.
- **ADR-003 / ADR-004 / ADR-005 / ADR-006** — untouched. The change is behaviour/config only at the HTTP-upgrade edge; the wire contract, session storage, provider auth, and permission model are unaffected.
- **ADR-033** — terminology only. The host is `Dmon.Network` (tool `ndmon`); the `Network:*` configuration surface (including the new `Network:AllowedOrigins`) and the `~/.dmon/network` state home follow that rename. The wire strings (`gw` discriminator, control frames) are unchanged, so no protocol bump.
