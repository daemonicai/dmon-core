# Design: dmail-oauth-hardening

## Context

`services/Dmail` is a standalone ASP.NET Core service (ADR-028) that links
Gmail accounts over Google OAuth2 (PKCE) and exposes an agent-facing `/api`
surface guarded by an app API key (`X-Api-Key`). The earlier
`services-security-lockdown` change hardened the API key to **default-deny
every `/api/*` path**. This change untangles a functional regression that
lockdown introduced on the OAuth path, and closes a still-present
Host-header defect (#11a) on the same path.

## Regression analysis (functional) — VERIFIED

**Middleware.** `services/Dmail/ApiKeyAuthExtensions.cs:12`:

```csharp
if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
// …validate X-Api-Key; 401 if missing/invalid, before any handler
```

Everything under `/api` requires `X-Api-Key`. There is **no** exemption other
than the non-`/api` fall-through.

**OAuth endpoints are under `/api`.** `services/Dmail/EndpointExtensions.cs`:
- Login: `app.MapGet("/api/auth/google/login", …)` (`:244`)
- Callback: `app.MapGet("/api/auth/google/callback", …)` (`:252`)

**Why the flow is broken:**
- The **callback** is invoked by Google redirecting the user's browser to
  `…/api/auth/google/callback?code=…&state=…`. A cross-site redirect from
  Google's consent screen **cannot** carry an `X-Api-Key` header. The
  middleware rejects it with 401 before the handler runs → the code is never
  exchanged → linking never completes.
- The **login** endpoint is reached by a top-level browser navigation from the
  dashboard (it issues `ctx.Response.Redirect(authUrl)` to Google). A browser
  navigation carries no custom header either, so it 401s the same way.

**Conclusion:** the OAuth account-linking flow is broken end-to-end after
`services-security-lockdown`. This is a genuine regression, not a hypothesis.

**Compensating control already exists.** The callback already validates state
and PKCE: `OAuth2StateStore.GetVerifier(state)` (`OAuth2StateStore.cs`) atomically
removes and returns the PKCE `code_verifier` bound to that `state` at login
time; the callback returns `400 invalid_state` when it is absent
(`EndpointExtensions.cs:261-265`) and only then calls
`oauth.ExchangeCodeAsync(code, codeVerifier, redirectUri)`. So exempting the
callback from the **app** API key does not leave it unauthenticated — it stays
protected by the OAuth `state` + PKCE handshake, which is the correct security
boundary for an OAuth redirect endpoint.

## Security defect #11a (Host-header `redirect_uri`) — VERIFIED

`EndpointExtensions.cs:246` (login) and `:267` (callback) both build:

```csharp
var redirectUri = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api/auth/google/callback";
```

`ctx.Request.Host` is attacker-influenceable (Host header). It flows into the
authorization URL (`OAuth2Service.BuildAuthorizationUrl`) and into the token
exchange body (`OAuth2Service.ExchangeCodeAsync`, `redirect_uri` form field).
The two must match, and the value must match a URI registered in the Google
console — so the fix must produce **one deterministic value from
configuration** for both handlers.

## Key decisions

### D1 — `redirect_uri` source: configured base URL, never `Host`

Introduce **`DMAIL_OAUTH_REDIRECT_BASE_URL`** (dedicated key; scheme + host +
optional port, no trailing path). The `redirect_uri` is
`{DMAIL_OAUTH_REDIRECT_BASE_URL}/api/auth/google/callback`, computed
identically in both the login and callback handlers.

- **Unset (default):** derive the base from the server's own known bind —
  loopback `http://127.0.0.1:{DMAIL_PORT}` — matching the loopback-by-default
  bind policy. This keeps the single-tenant local deployment working with no
  new config while **never** reading `Host`.
- **Container / non-loopback deployment:** operator sets
  `DMAIL_OAUTH_REDIRECT_BASE_URL` to the externally-reachable base (the same
  value registered in the Google console).

Rationale for a dedicated key over reusing a generic `DMAIL_BASE_URL`: the
OAuth redirect origin can legitimately differ from any other notion of "base
URL" (e.g. behind a proxy), and a purpose-named key documents intent. This is
an implementation-detail decision, not a spec constraint — the spec requires
only that the value come from configuration, not the Host header.

### D2 — Exempt exactly the two OAuth paths

Exempt `GET /api/auth/google/login` and `GET /api/auth/google/callback` from
the `X-Api-Key` check in `ApiKeyAuthExtensions` — matched as an **exact,
closed allow-list**, not a prefix wildcard, so no other `/api/auth/*` or
future `/api` route is accidentally opened. Every other `/api/*` route stays
default-deny. Prefer `StartsWithSegments` on each full path (segment-boundary
match) to avoid substring bypasses.

### D3 — State/PKCE validation is the compensating control (make it explicit)

The exemption is only sound because the callback validates `state`→PKCE before
exchanging the code. The spec delta elevates this from an incidental behaviour
to an explicit **requirement**: the callback SHALL reject a request whose
`state` has no stored PKCE verifier and SHALL NOT exchange the authorization
code in that case. The current code already does this; the requirement fences
it so a future refactor cannot silently drop it and turn the exemption into an
open redirect/CSRF hole.

## Risks

- **Over-broad exemption.** If the exemption were written as a prefix
  (`/api/auth`) rather than the two exact paths, a future sibling route would
  inherit the exemption. Mitigated by D2's closed allow-list.
- **`redirect_uri` mismatch.** Login and callback must compute the identical
  value; a divergence causes Google `redirect_uri_mismatch`. Mitigated by a
  single shared helper and a test asserting both handlers agree.
- **Misconfigured base URL in production.** A wrong
  `DMAIL_OAUTH_REDIRECT_BASE_URL` breaks linking but fails safe (no token
  issued). Documented in the deployment impact.

## Stop-and-ask — spec carve-out (CONFIRM BEFORE IMPLEMENTING)

The standing `dmail-server` spec's requirement **"Agent-facing HTTP API
requires an API key"** was authored by `services-security-lockdown` and is
worded deliberately and absolutely:

> "The server SHALL require a valid API key (`X-Api-Key`) on **every**
> `/api/*` endpoint (default-deny), including … `GET /api/auth/google/login`,
> and `GET /api/auth/google/callback`. … Only `GET /health` and the static
> dashboard files are exempt."

It **explicitly enumerates the two OAuth endpoints as requiring the key** and
**explicitly closes the exemption set** to just `/health` + static files.
Carving out the OAuth endpoints directly contradicts that wording — it is a
security-relevant reversal of a decision that change made on purpose.

This change treats it as a **spec refinement** (a MODIFIED requirement that
re-states the block with the OAuth endpoints moved from the "included" list to
the "exempt" list, justified by the OAuth state+PKCE compensating control).
The refinement is safe and is standard practice for OAuth redirect endpoints.
**However, because the original wording is deliberately absolute and
security-critical, the user must confirm the carve-out before the change is
implemented.** If the user does **not** approve exempting these endpoints, the
functional regression cannot be fixed as designed and an alternative (e.g.
moving the OAuth endpoints off the `/api` prefix, which is a larger contract
change) would be required — stop and re-scope.

No ADR conflict: ADR-028 governs the Dmail service topology, not this
endpoint-auth detail; this refines `services-security-lockdown`, which no ADR
freezes.
