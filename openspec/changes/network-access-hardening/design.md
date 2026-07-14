# Design — network-access-hardening

## Context

`Dmon.Network` is the single-tenant WebSocket remote host (ADR-012). Its `/ws` upgrade edge (`NetworkConnectionEndpoint.HandleAsync`) does two access-control checks conceptually:

1. **Device-key (Bearer) auth** — `DeviceKeyAuthenticator.Authenticate(authHeader, keySet)`. When the active set is **non-empty**, the presented `Authorization: Bearer <token>` is SHA-256-hashed and constant-time-compared against each active entry (ADR-018 D1/D3). When the set is **empty**, it short-circuits to `AuthorizedNoKey` — auth disabled (ADR-018 D2).
2. **Bind policy** — enforced once at startup in `Program.cs` via `NetworkBindPolicy.Validate`. Loopback is always allowed; wildcard/all-interfaces (`0.0.0.0`, `::`, `*`, `+`) is always rejected; a specific non-loopback address is allowed **only** when `Network:AllowNonLoopbackBind=true`.

The two findings are both at this edge:

- **#6** The empty-set short-circuit is unconditional. It is the right default for the intended deployment — loopback bind fronted by `tailscale serve`, where "reaching the socket" already means "on the tailnet" and the operator cannot lock themselves out (ADR-012 D12, ADR-018 D2). But the code also permits a **non-loopback** bind (the `AllowNonLoopbackBind` escape hatch). On that bind an empty `devices.json` authorizes every upgrade from anything that can route to the address — the bind gate never gated auth.
- **#17** `HandleAsync` reads only the `Authorization` header before `AcceptWebSocketAsync`. Browsers do not apply same-origin policy to WebSocket upgrades, and a cross-site page that can reach the host can open `/ws`. There is no `Origin` allowlist.

## Goals

- Empty/absent device-key set fails **closed** (rejects `/ws`) when the effective bind is **non-loopback**; retains fail-open only on a **loopback** bind.
- A browser-originated (`Origin`-bearing) upgrade is rejected unless its origin is explicitly allowlisted; native (origin-less) clients are unaffected.
- Both checks are additive defence-in-depth over the existing Bearer auth; neither weakens the non-empty device-key path.

## Non-goals

- No change to the non-empty device-key auth path, the file-backed store, hot-reload, revocation-fencing, or `lastseen.json` (ADR-018 D1/D3–D7).
- No change to the ADR-003 wire shape, control frames, or session lifecycle.
- Not building a CORS framework, per-origin credentials, or origin-scoped session partitioning. The allowlist is an exact-match string set.
- Not addressing #14 (`_admittedIds` unbounded growth) — out of scope for this change.

## Key decisions

### D1 — Effective-bind condition for empty-set fail-closed

The fail-closed trigger is: **the effective bind is non-loopback AND the active device-key set is empty.** "Effective bind is non-loopback" is exactly `NetworkBindPolicy.IsNonLoopbackWithOptIn(BindAddress, AllowNonLoopbackBind)` — a specific non-loopback address with the opt-in set. This is well-defined because the only two states that survive startup validation are (i) loopback and (ii) a specific non-loopback address with `AllowNonLoopbackBind=true`; wildcard binds are already fatal at startup (`NetworkBindPolicy.Validate`). So "not loopback" ⇒ "non-loopback opt-in", with no third case.

- **Non-loopback + empty set →** reject the `/ws` upgrade with **HTTP 401** before any socket is opened (mirrors the existing unknown/absent-token rejection path). Do **not** return `AuthorizedNoKey`.
- **Loopback + empty set →** retain today's `AuthorizedNoKey` (local-dev convenience; the operator cannot lock themselves out on their own box — ADR-018 D2's guarantee, preserved for the case it was written for).
- **Non-empty set (any bind) →** unchanged: per-device Bearer auth as today.

**Where the decision lives.** `DeviceKeyAuthenticator.Authenticate` is a pure static over `(authHeader, keySet)` and has no bind context. Two acceptable shapes for the worker (choose at implementation, spec is agnostic): (a) the endpoint computes the effective-bind flag once (from `IOptionsMonitor<NetworkOptions>.CurrentValue` via `IsNonLoopbackWithOptIn`) and, when the authenticator returns `AuthorizedNoKey` **and** the bind is non-loopback, overrides to a 401; or (b) pass the flag into the authenticator so the empty-set branch returns `NotAuthorized` when non-loopback. Either keeps the non-empty path byte-identical. The startup "auth disabled (fail-open on absent…)" log in `Program.cs` becomes conditional — logged only when the effective bind is loopback; a non-loopback bind with an empty store logs that auth is fail-closed until a device is paired.

### D2 — `Origin` allowlist semantics and config source

Evaluated in `HandleAsync` **before** `AcceptWebSocketAsync`, and independent of the Bearer check (both must pass):

- **No `Origin` header →** allowed to proceed to Bearer auth. Native clients (`URLSessionWebSocketTask`, desktop `ClientWebSocket`) send no `Origin`; this path must remain open or every shipped client breaks.
- **`Origin` header present →** allowed only if it **exactly** matches an entry in the configured allowlist; otherwise reject with **HTTP 403** before any socket is opened. Matching is exact string comparison against `Network:AllowedOrigins` (ordinal; an operator lists the precise `scheme://host[:port]` origins a trusted browser app serves from).
- **Default empty allowlist →** every `Origin`-bearing (browser) upgrade is rejected. This is fail-closed-by-default: browser access is opt-in.

**Config source.** `Network:AllowedOrigins` is a new `NetworkOptions` property (a `string[]`, default empty), bound from the `Network` configuration section — the same `Network:*` surface as `BindAddress`, `AllowNonLoopbackBind`, etc. The network host's on-disk config/state home is `~/.dmon/network` (where `devices.json`/`lastseen.json` live); `Network:AllowedOrigins` is set through the host's normal configuration (appsettings/`NETWORK__ALLOWEDORIGINS__n` env) rather than a separate file, keeping one option surface.

**Why 403 (not 401) for a bad origin.** A rejected origin is not an authentication failure (no credential was wrong); it is a forbidden caller class. 401 is reserved for the device-key path; 403 distinguishes "your browser origin is not permitted" from "your token is missing/invalid", which also avoids a browser silently retrying with credentials.

### D3 — Ordering and interaction of the two checks

Both checks run before `AcceptWebSocketAsync`, so an unauthorized/forbidden caller never opens a socket (same property the current auth check has). The `Origin` check and the Bearer/empty-set check are independent gates: a request must pass **both**. Order is an implementation detail (the spec requires only that both are enforced before upgrade); evaluating `Origin` first is natural since it needs no store lookup.

### D4 — ADR-012 / ADR-018 amendment note (ADR-036)

- **ADR-018 D2** currently states the empty set means "disabled … every upgrade authorized over the tailnet … an operator cannot lock themselves out." This change **narrows** D2: empty-set-disabled applies on a **loopback** bind; on a **non-loopback** bind an empty set fails **closed**. This preserves D2's actual guarantee (loopback/tailnet: no lockout) and only closes the non-loopback case D2's rationale ("over the tailnet") never covered. It is consistent with ADR-018 **D5**, which already mandates fail-closed ("never fail open to 'disabled'") for the malformed-store case — this extends the same fail-closed posture to the empty-set-on-exposed-bind case.
- **ADR-012 D12** lists an *optional shared key* as the only defence-in-depth atop Tailscale. This change **adds** an `Origin` allowlist as a second, browser-specific defence-in-depth control at the same upgrade edge. It does not touch the transport, the single-tenant model, or the "loopback + `tailscale serve`, never a public NIC" posture — a non-loopback bind remains an operator opt-in, now with auth that fails closed when unconfigured.

Neither is a contradiction, so no superseding ADR is required — an **amending** ADR-036 (status *Accepted*, `Amends: ADR-018 (D2), ADR-012 (D12)`) records both, following the ADR-018-amends-ADR-012 precedent. ADR-036 must be accepted before the code lands (task 1.1).

## Risks

- **Locking out a legitimately-exposed operator.** An operator who runs a non-loopback bind with no `devices.json` will start seeing 401s. This is the intended fix, but it is a behaviour change for that (unusual, opt-in) configuration; the startup log must state clearly that auth is fail-closed until a device is paired. Mitigation: the loopback default is unchanged, so the common path is unaffected.
- **Breaking a browser client that relied on the missing check.** No first-party browser client ships today (clients are iOS/desktop, origin-less), so default-empty allowlist rejecting browser origins breaks nothing shipped; a future browser frontend must add its origin to `Network:AllowedOrigins`.
- **Origin spoofing by non-browser callers.** `Origin` is trivially forgeable by a non-browser client, so this is *not* an auth control — it is defence-in-depth against the one attacker who cannot forge it (a real browser under the same-origin/Origin-setting rules). The spec frames it exactly as such, layered atop Bearer auth.
