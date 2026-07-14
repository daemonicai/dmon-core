## Why

Two access-control gaps in the `Dmon.Network` WebSocket remote host let an operator's misconfiguration or a browser page reach the `/ws` upgrade with more privilege than intended. Both are verified present on `main`.

- **#6 — Gateway auth fails *open* on an empty/absent `devices.json`.** `frontends/Dmon.Network/DeviceKeys/DeviceKeyAuthenticator.cs:32-34` short-circuits `if (keySet.IsEmpty) return DeviceAuthResult.AuthorizedNoKey;` — an empty or absent device-key store authorizes **every** `/ws` upgrade regardless of the `Authorization` header (`Program.cs:53` even records "auth disabled (fail-open on absent…)"). The only mitigation today is at the **bind** layer: `NetworkBindPolicy.Validate` refuses a non-loopback bind unless `Network:AllowNonLoopbackBind=true`. That gate protects the *bind*, not *auth*: once an operator opts into a non-loopback bind (a specific Tailscale/LAN address), an empty `devices.json` is wide open to everything that can reach that address. The empty-set-disabled rule is correct for the intended loopback + `tailscale serve` deployment (ADR-012 D12, ADR-018 D2 — "the operator cannot lock themselves out over the tailnet"), but it must not remain fail-open once the host is listening on a non-loopback interface.

- **#17 — WebSocket `/ws` has no `Origin` check.** `frontends/Dmon.Network/NetworkConnectionEndpoint.cs:159-181` (`HandleAsync`) reads only `context.Request.Headers.Authorization` before `AcceptWebSocketAsync` — there is no `Origin` allowlist. Native clients (iOS, desktop) send **no** `Origin` header, but a browser tab on any origin *does*, and the browser same-origin policy does not block cross-site WebSocket upgrades. A malicious page loaded in a browser that can route to the host (e.g. over the tailnet) could open `/ws` and drive a session. There is no defence-in-depth against a browser-originated cross-site WebSocket today.

Both are "reject before you accept" gaps at the network host's HTTP-upgrade edge: the correct outcome is fail closed (empty key set on a non-loopback bind rejects; an unlisted browser `Origin` rejects), while preserving the local-dev and native-client paths that legitimately carry no credential/no origin.

## What Changes

- **#6 — Empty key set fails closed on a non-loopback bind.** When the **effective bind is non-loopback** (a specific non-loopback address, permitted only via `Network:AllowNonLoopbackBind`; wildcard/all-interfaces binds are already rejected at startup) **and** the active device-key set is empty or absent, the network host SHALL reject every `/ws` upgrade with HTTP 401 before any socket is opened — it SHALL NOT authorize via the empty-set short-circuit. When the effective bind is **loopback**, the current empty-set-disabled behaviour is **retained** (local-dev convenience; the operator cannot lock themselves out on their own box, matching ADR-018 D2). A **non-empty** active key set continues to enforce per-device Bearer auth exactly as today, on any bind.
- **#17 — `Origin` allowlist for browser-originated upgrades.** Before `AcceptWebSocketAsync`, the network host SHALL evaluate the `Origin` request header. A request with **no** `Origin` header (native clients — iOS, desktop) SHALL be allowed to proceed to the existing Bearer-auth check. A request **with** an `Origin` header SHALL be rejected with HTTP 403 unless the origin exactly matches a configured allowlist. The allowlist is sourced from the network host configuration (`Network:AllowedOrigins`, the same `Network:*` surface as the other network options; the host's on-disk config/state home is `~/.dmon/network`) and defaults to **empty**, so by default every browser `Origin` is rejected. The `Origin` check is **defence-in-depth atop** the existing Bearer/device-key auth — both checks apply; passing one does not waive the other.

No wire-protocol shape change (ADR-003), no control-frame change, no session-storage change, no change to the non-empty device-key auth path or the file-backed store / hot-reload / revocation-fencing behaviour (ADR-018). The changes live entirely at the `/ws` HTTP-upgrade edge.

## Capabilities

### New Capabilities

_None — this change hardens two access-control behaviours of an existing capability; it introduces no new capability._

### Modified Capabilities

- `remote-session-gateway`: **MODIFIED** — (a) the "Tailscale-fronted authentication" requirement's empty-key-set-disabled rule is qualified to a **loopback** bind; on a non-loopback bind an empty/absent key set fails **closed** (new requirement); (b) a new `Origin`-allowlist requirement adds defence-in-depth against browser-originated cross-site WebSocket upgrades, allowing origin-less native clients and rejecting unlisted browser origins by default.

## Impact

- **Code:**
  - `frontends/Dmon.Network/NetworkConnectionEndpoint.cs` — evaluate the effective-bind + empty-set condition and the `Origin` header in `HandleAsync` before `AcceptWebSocketAsync`.
  - `frontends/Dmon.Network/DeviceKeys/DeviceKeyAuthenticator.cs` and/or the endpoint — thread the effective-bind decision into the empty-set short-circuit (the authenticator currently has no bind context).
  - `frontends/Dmon.Network/NetworkOptions.cs` — new `AllowedOrigins` option (default empty); the effective-bind determination reuses `NetworkBindPolicy.IsNonLoopbackWithOptIn(BindAddress, AllowNonLoopbackBind)`.
  - `frontends/Dmon.Network/Program.cs` — the startup "auth disabled" log becomes conditional (only when the effective bind is loopback); a non-loopback bind with an empty store logs fail-closed.
- **Tests:** `test/Dmon.Network.Tests` — empty-set-on-non-loopback rejects, empty-set-on-loopback still authorizes, non-empty set unchanged on any bind; origin-less request allowed, unlisted browser origin rejected (default empty), allowlisted origin passes to Bearer auth, origin check independent of Bearer auth.
- **ADRs:** amends **ADR-018 D2** (empty-set-disabled narrowed to a loopback bind) and **ADR-012 D12** (adds the `Origin` allowlist as defence-in-depth alongside the optional key). A new **ADR-036** records the amendment and MUST be accepted before implementation (task 1.1). No ADR is contradicted — the change is consistent with ADR-018 D5's fail-closed intent and ADR-012's zero-public-exposure posture.
- **No impact:** the ADR-003 wire shape, control frames, session storage (ADR-004), provider auth (ADR-005), the permission model (ADR-006), the non-empty device-key auth path, the file-backed store, hot-reload, and revocation-fencing (ADR-018 D1/D3–D7). Out of scope: every other audit finding, including #14 (`_admittedIds` growth).
