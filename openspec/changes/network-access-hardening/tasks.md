## 1. ADR amendment (prerequisite)

- [ ] 1.1 Write `docs/adrs/ADR-036-network-access-hardening.md` (status **Accepted**, `Amends: ADR-018 (Decision 2), ADR-012 (Decision 12)`) recording: (a) empty/absent device-key set is auth-disabled **only on a loopback bind**; on a non-loopback bind it fails **closed** (HTTP 401), consistent with ADR-018 D5's fail-closed intent and ADR-012's zero-public-exposure posture; (b) an `Origin` allowlist is added as browser-specific defence-in-depth at the `/ws` upgrade edge, alongside ADR-012 D12's optional key. Use the existing ADRs as the format template. This ADR MUST be accepted before the code changes below land.

## 2. Empty key set fails closed on a non-loopback bind (#6)

- [ ] 2.1 Investigate the current fail-open path: confirm `frontends/Dmon.Network/DeviceKeys/DeviceKeyAuthenticator.cs:32-34` returns `DeviceAuthResult.AuthorizedNoKey` when `keySet.IsEmpty` unconditionally; confirm `NetworkConnectionEndpoint.HandleAsync` (`frontends/Dmon.Network/NetworkConnectionEndpoint.cs:159-171`) uses that result verbatim; confirm the "effective bind is non-loopback" predicate is exactly `NetworkBindPolicy.IsNonLoopbackWithOptIn(NetworkOptions.BindAddress, NetworkOptions.AllowNonLoopbackBind)` and that wildcard binds are already fatal in `Program.cs` startup validation (so only loopback and non-loopback-opt-in reach the endpoint).
- [ ] 2.2 Make the empty-set short-circuit bind-aware: when the active key set is empty **and** the effective bind is non-loopback, reject the `/ws` upgrade with HTTP 401 before any socket is opened (do not authorize). When the bind is loopback, retain today's `AuthorizedNoKey`. Keep the non-empty device-key path byte-identical. Thread the effective-bind flag either at the endpoint (override `AuthorizedNoKey`→401 when non-loopback) or into the authenticator — implementer's choice; the non-empty path must not change.
- [ ] 2.3 Update `frontends/Dmon.Network/Program.cs`: make the startup "auth disabled (fail-open on absent…)" log conditional on a **loopback** effective bind; on a non-loopback bind with an empty/absent store, log that authentication is **fail-closed** until a device is paired (not "disabled").

## 3. Browser-Origin allowlist on the WebSocket upgrade (#17)

- [ ] 3.1 Add `AllowedOrigins` to `frontends/Dmon.Network/NetworkOptions.cs`: a `string[]` bound from the `Network` config section, defaulting to empty. Document it (default empty ⇒ all browser `Origin`s rejected; the network host config/state home is `~/.dmon/network`).
- [ ] 3.2 In `NetworkConnectionEndpoint.HandleAsync`, before `AcceptWebSocketAsync` and independent of the device-key check, evaluate the `Origin` request header: no `Origin` header ⇒ proceed (native clients); an `Origin` present ⇒ reject with HTTP 403 unless it exactly matches an entry in `AllowedOrigins`. Both the `Origin` check and the device-key check must pass before upgrade; neither waives the other.

## 4. Tests

- [ ] 4.1 In `test/Dmon.Network.Tests`, add empty-set / bind tests: (a) empty set + non-loopback effective bind ⇒ `/ws` upgrade rejected 401, no socket; (b) empty set + loopback bind ⇒ authorized (unchanged); (c) non-empty set enforced identically on a non-loopback bind (matching token authorized+tagged; missing/unmatched token 401).
- [ ] 4.2 In `test/Dmon.Network.Tests`, add `Origin`-allowlist tests: (a) no `Origin` header ⇒ proceeds to auth (allowed); (b) `Origin` present + empty allowlist ⇒ 403; (c) `Origin` present + exact match in allowlist ⇒ proceeds; (d) allowlisted `Origin` but failing device-key check (non-empty set, bad token) ⇒ still 401 (checks independent, both enforced).

## 5. Gates and spec alignment

- [ ] 5.1 `make build` clean (TreatWarningsAsErrors on).
- [ ] 5.2 `env -u MEKO_API_KEY make test` green — the new tests plus all existing tests.
- [ ] 5.3 `openspec validate network-access-hardening --strict` passes; the `remote-session-gateway` delta (one MODIFIED requirement, two ADDED requirements) matches the implemented behaviour, and ADR-036 is accepted and referenced.
