# DEVLOG — dmail-oauth-hardening

Cross-block memory for the architect. Newest block first.

## Block 2 — API-key OAuth exemption + state fence (final) — tasks 2.1, 3.1, 4.1, 4.2, 4.3, 4.5

**Status:** DONE, reviewer approved, gates green. **Final block — change complete (all 15 tasks ticked).**

### What landed
- `services/Dmail/ApiKeyAuthExtensions.cs` (production) — added a closed 2-path allow-list (`/api/auth/google/login`, `/api/auth/google/callback`) + `IsOAuthEntryPoint` helper. Exemption sits **after** the `/api` gate and **before** key validation; GET-only (`HttpMethods.IsGet`) + exact (`StartsWithSegments(exempt, OrdinalIgnoreCase, out remaining) && !remaining.HasValue`). So `/api/auth/google/login/evil`, `/api/auth/google/callbackXYZ`, `/api/auth/other`, and any non-GET verb all fall through to default-deny 401.
- `test/Dmail.Tests/ApiKeyAuthMiddlewareTests.cs` — `BuildContext` gained a `method` param (default `"GET"`; `DefaultHttpContext.Request.Method` defaults to `""`). Removed `/api/auth/google/login` from the 401 theory (H1); added decoys (`/api/auth/other`, `.../login/evil`, `.../callbackXYZ`) + a non-GET-on-exempt-path 401 theory + a passing exempt-path theory.
- `test/Dmail.Tests/ApiKeyAuthIntegrationTests.cs` — `OAuthCallback_NoKey_ReachesHandlerAndRejectsUnknownState` (no key + unknown state ⇒ **400 invalid_state**, not 401 — proves exemption wired + fence intact; passes BOTH `code` & `state` to avoid a binding-400 masquerade), `OAuthLogin_NoKey_ReachesHandlerAndRedirects` (302; sets `DMAIL_GOOGLE_CLIENT_ID` in fixture setup, null'd in Dispose), `OtherApiAuthPath_NoKey_Returns401`.
- `test/Dmail.Tests/OAuth2StateStoreTests.cs` (NEW) — single-use/replay: second `GetVerifier(state)` ⇒ null. Dmail.Tests now 118.

### 3.1 finding
**Confirmed — NO code change.** `EndpointExtensions.cs:261-265` looks up `store.GetVerifier(state)` → `400 invalid_state` before any `ExchangeCodeAsync`; `OAuth2StateStore.GetVerifier` uses `TryRemove` (single-use). Left byte-identical.

### Reviewer nits (NOT applied, non-blocking)
- Exemption is `OrdinalIgnoreCase` ⇒ `/API/AUTH/...` also exempt. Harmless — ASP.NET routing is case-insensitive anyway, so matching middleware to routing is correct.
- `HttpMethods.IsGet` excludes HEAD ⇒ HEAD on exempt path ⇒ 401. Acceptable (stricter than GET-only spec).

### Change complete
Both blocks reviewer-approved; full gates green (Dmail.Tests 118, full suite pass, validate --strict). Spec delta (`specs/dmail-server/spec.md`): 1 MODIFIED (API-key exemption carve-out) + 2 ADDED (redirect-uri = block 1, state/PKCE fence = this block) — all match implemented behaviour (gate 5.3). Next: push + PR; then batch `/opsx:archive` all outstanding (this change + #96/#97/#98 + network-access-hardening #99).

**Carve-out approval:** The §2 OAuth path-exemption BLOCKER (modifies the deliberately-absolute "only /health + dashboard are exempt" wording from `services-security-lockdown`) is **user-approved** (confirmed this session). Not a blocker — proceed.

## Block 1 — OAuth redirect_uri from configuration (#11a) — tasks 1.1, 1.2, 1.3, 1.4, 4.4

**Status:** DONE, reviewer approved, gates green.

### What landed
- `services/Dmail/EndpointExtensions.cs` — new `internal static string ResolveOAuthRedirectUri(IConfiguration config)` (~:290): reads `DMAIL_OAUTH_REDIRECT_BASE_URL`; `string.IsNullOrWhiteSpace` ⇒ fallback `http://127.0.0.1:{DMAIL_PORT ?? "8080"}`; `TrimEnd('/')` on the configured base; appends `/api/auth/google/callback`. Takes **no HttpContext** — that IS the security property. Both login (~:246) and callback (~:267) handlers now inject `IConfiguration` and call this one helper; `ctx.Request.Host`/`Scheme` gone from both `redirectUri` builds. Login lambda still has `HttpContext ctx` but only for `ctx.Response.Redirect(authUrl)` — legit.
- `services/Dmail/appsettings.json` — added `"DMAIL_OAUTH_REDIRECT_BASE_URL": ""`.
- `services/Dmail/README.md` — documented the new key in the Configuration table.
- `test/Dmail.Tests/OAuthRedirectUriTests.cs` (NEW) — 9 tests: configured base wins, trailing-slash trim, empty/whitespace/unset fallback (127.0.0.1, default 8080), spoofed-Host-no-influence (structural), login==callback identity. Dmail.Tests now 105.

### Decisions / notes
- Helper is `internal static`; `Dmail.Tests` calls it via existing `InternalsVisibleTo`.
- Callback's `store.GetVerifier(state)` → `invalid_state` 400 guard (~:261–265) left **byte-identical** — it's task 3.1's fence (block 2).
- Reviewer optional nit (NOT applied): `LoginAndCallback_ComputeIdenticalValue` asserts equality of two calls to the same pure helper — a tautology; could assert against literal expected strings instead. Harmless; structural identity verified by inspection.

## Block 2 (remaining) — API-key exemption + state fence — tasks 2.1, 3.1, 4.1, 4.2, 4.3, 4.5
- **2.1:** in `services/Dmail/ApiKeyAuthExtensions.cs` (`InvokeAsync`, `StartsWithSegments("/api")` default-deny), exempt EXACTLY `GET /api/auth/google/login` and `GET /api/auth/google/callback` via an exact closed allow-list (segment-boundary match, method-checked). Every other `/api/*` stays default-deny.
- **3.1:** confirm/keep the callback state+PKCE compensating control (`OAuth2StateStore.GetVerifier`, single-use `TryRemove`, unknown state ⇒ 400 `invalid_state`, no code exchange). Adjust only if the exemption changed the flow (it shouldn't).
- **HAZARD for block 2:** the existing `test/Dmail.Tests/ApiKeyAuthMiddlewareTests.cs` `NoKey_ApiPath_Returns401AndDoesNotCallNext` `[Theory]` includes `[InlineData("/api/auth/google/login")]` — the §2 exemption WILL break that inline case. Block 2 owns updating that test (remove/relocate the two exempted paths from the 401 theory; add them as passing cases). Block 1 left it untouched/green.
- **Tests:** 4.1 callback no-key ⇒ NOT 401 (reaches handler, unknown state ⇒ 400 invalid_state); 4.2 login no-key ⇒ NOT 401 (issues Google redirect); 4.3 non-exempt `/api/*` (e.g. `/api/status`) AND decoy `/api/auth/other` no-key ⇒ still 401; 4.5 callback unknown/consumed state ⇒ 400 invalid_state, no token exchange (single-use/non-replayable).
- Spec delta (`specs/dmail-server/spec.md`): 1 MODIFIED requirement (the exemption carve-out) + the 2 ADDED (redirect-uri = block 1, state/PKCE compensating control). Gate 5.3 (spec-matches-impl) is the orchestrator's at change end.
