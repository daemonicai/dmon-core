# Change: dmail-oauth-hardening

## Why

Two defects sit on the Dmail OAuth callback path, one functional and one security:

1. **Functional regression (from `services-security-lockdown`).** That earlier
   change made `ApiKeyAuthExtensions` default-deny **every** path under `/api`
   (`ApiKeyAuthExtensions.cs:12`, `StartsWithSegments("/api")`) with no
   exemption, requiring an `X-Api-Key` header. The Google OAuth
   login/callback endpoints live under that prefix
   (`GET /api/auth/google/login`, `GET /api/auth/google/callback`). Google
   redirects the browser to the callback and **cannot** attach an
   `X-Api-Key` header; the initial login is likewise a top-level browser
   navigation from the dashboard that carries no header. Both requests are
   therefore rejected with HTTP 401 **before any handler runs**, so account
   linking is broken end-to-end. This is verified below.

2. **Security defect (#11a — still present).** The OAuth `redirect_uri` is
   built from the inbound request Host header
   (`EndpointExtensions.cs:246` and `:267`:
   `$"{ctx.Request.Scheme}://{ctx.Request.Host}/api/auth/google/callback"`).
   A manipulated `Host` header can influence the `redirect_uri` presented to
   Google and echoed into the token exchange.

The fix must **restore the flow** by exempting exactly the OAuth login and
callback paths from the app API key, while keeping every other `/api` route
default-deny, and must **close the Host-header hole** by sourcing the
`redirect_uri` from configuration. The exemption is only safe because the
callback is already protected by OAuth `state` + PKCE; this change makes that
protection an explicit, non-negotiable precondition of the exemption.

## What Changes

- **Fix the functional regression (primary):** exempt exactly
  `GET /api/auth/google/login` and `GET /api/auth/google/callback` from the
  `X-Api-Key` requirement so Google and the browser can reach them. Every
  other `/api/*` route remains default-deny.
- **Close #11a:** build the OAuth `redirect_uri` from a configured base URL
  (`DMAIL_OAUTH_REDIRECT_BASE_URL`), never from the request `Host` header.
  When unset, derive it from the server's own known loopback bind and
  `DMAIL_PORT` — still never from `Host`.
- **Make the exemption safe:** require the callback to validate the OAuth
  `state` (and its bound PKCE verifier) and refuse the code exchange when
  validation fails, as the compensating control for the API-key exemption.

> **Spec-refinement note (requires confirmation).** The standing
> `dmail-server` spec, as written by `services-security-lockdown`, explicitly
> enumerates the two OAuth endpoints as requiring `X-Api-Key` and states that
> "Only `GET /health` and the static dashboard files are exempt." Carving out
> the OAuth endpoints **modifies** that deliberately-absolute wording. See
> `design.md` → *Stop-and-ask*. This proposal assumes the carve-out is
> approved.

## Capabilities

### Modified Capabilities

- **dmail-server** — the API-key requirement is refined to exempt the two
  OAuth entry points; the OAuth linking flow gains a configured-`redirect_uri`
  requirement and an explicit state/PKCE compensating-control requirement.

## Impact

- **Affected specs:** `dmail-server` (1 MODIFIED requirement, 2 ADDED
  requirements).
- **Affected code:** `services/Dmail/ApiKeyAuthExtensions.cs` (path exemption),
  `services/Dmail/EndpointExtensions.cs` (`redirect_uri` from config in both
  login and callback handlers; keep the existing state/PKCE check),
  `services/Dmail/Program.cs` / `appsettings.json` (new config key).
- **ADR:** ADR-028 governs `services/Dmail`; this is a refinement of the
  `services-security-lockdown` lockdown, not a new architecture — no ADR
  conflict.
- **Deployment:** operators of the non-loopback (containerised) deployment
  must set `DMAIL_OAUTH_REDIRECT_BASE_URL` to their externally-reachable base
  URL; local loopback deployments need no new configuration.
