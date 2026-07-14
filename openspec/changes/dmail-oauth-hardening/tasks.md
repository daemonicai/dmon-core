# Tasks: dmail-oauth-hardening

> BLOCKER — confirm before starting: the OAuth path exemption modifies a
> deliberately-absolute wording added by `services-security-lockdown`
> (see `design.md` → Stop-and-ask). Do not implement §2 until the user
> confirms the carve-out.

## 1. Redirect URI from configuration (#11a)

- [ ] 1.1 Add a helper (single source of truth) that computes the OAuth base URL from `DMAIL_OAUTH_REDIRECT_BASE_URL`, falling back to `http://127.0.0.1:{DMAIL_PORT}` (loopback) when unset — never from `ctx.Request.Host`.
- [ ] 1.2 Replace the `Host`-derived `redirectUri` in the login handler (`EndpointExtensions.cs:246`) with the helper-computed value.
- [ ] 1.3 Replace the `Host`-derived `redirectUri` in the callback handler (`EndpointExtensions.cs:267`) with the same helper-computed value, so login and callback are identical.
- [ ] 1.4 Register `DMAIL_OAUTH_REDIRECT_BASE_URL` in `appsettings.json` (empty default) and document it in `services/Dmail/README.md`.

## 2. OAuth path exemption from the app API key (functional regression fix)

- [ ] 2.1 In `ApiKeyAuthExtensions.InvokeAsync`, exempt exactly `GET /api/auth/google/login` and `GET /api/auth/google/callback` from the `X-Api-Key` check using an exact, closed allow-list (segment-boundary match), leaving every other `/api/*` path default-deny.

## 3. Callback state/PKCE compensating control

- [ ] 3.1 Confirm the callback rejects an unknown `state` with `400 invalid_state` and does not exchange the code (existing behaviour via `OAuth2StateStore.GetVerifier`); keep the single-use (`TryRemove`) semantics so a `state` cannot be replayed. Adjust only if the exemption changed the flow.

## 4. Tests

- [ ] 4.1 Test: `GET /api/auth/google/callback` with **no** `X-Api-Key` is NOT rejected with 401 by the middleware (reaches the handler; unknown state → 400 `invalid_state`).
- [ ] 4.2 Test: `GET /api/auth/google/login` with **no** `X-Api-Key` is NOT rejected with 401 (reaches the handler / issues the Google redirect).
- [ ] 4.3 Test: a non-exempt `/api/*` path (e.g. `GET /api/status`, and a decoy `/api/auth/other`) with no `X-Api-Key` still returns 401.
- [ ] 4.4 Test: the computed `redirect_uri` uses `DMAIL_OAUTH_REDIRECT_BASE_URL` when set and ignores a spoofed `Host` header; falls back to `http://127.0.0.1:{DMAIL_PORT}` when unset — asserted for both the login and callback handlers (identical value).
- [ ] 4.5 Test: callback with an unknown/consumed `state` returns 400 `invalid_state` and performs no token exchange (state is single-use / non-replayable).

## 5. Gates

- [ ] 5.1 `make build` clean (no warnings; `TreatWarningsAsErrors`).
- [ ] 5.2 `env -u MEKO_API_KEY make test` green (new tests + all existing).
- [ ] 5.3 `openspec validate dmail-oauth-hardening --strict` passes.
