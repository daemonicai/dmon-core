# dmail-server (delta: dmail-oauth-hardening)

## MODIFIED Requirements

### Requirement: Agent-facing HTTP API requires an API key

The server SHALL require a valid API key (`X-Api-Key`) on **every** `/api/*` endpoint (default-deny), including — but not limited to — `POST /api/search`, `POST /api/emails/list`, `GET /api/emails/{uid}`, `GET /api/status`, `GET /api/accounts`, `DELETE /api/accounts/{email}`, and `POST /api/accounts/{email}/sync`. A request to any protected `/api/*` endpoint with a missing or incorrect key SHALL be rejected with HTTP 401 before any handler logic runs. Only `GET /health`, the static dashboard files, and the two OAuth entry points `GET /api/auth/google/login` and `GET /api/auth/google/callback` are exempt. The OAuth entry points are exempt because they are reached by browser navigations and by Google's redirect, which cannot present the app API key; their security boundary is the OAuth `state` + PKCE handshake (see "OAuth callback validates state and PKCE before exchanging the authorization code"), not `X-Api-Key`. The exemption SHALL be an exact, closed allow-list of exactly those two paths — no other `/api/*` route (including any other `/api/auth/*` path) SHALL be exempt. The key SHALL be validated with a constant-time comparison.

When `DMAIL_API_KEY` is unset, the server SHALL auto-generate a key on first run, persist it to a file under `DMAIL_DATA_DIR/keys/` with owner-only (`0600`) permissions, reuse that persisted key on subsequent restarts, and log only the file **path** — never the key value.

The `GET /api/emails/{uid}` endpoint SHALL return the full message body, and SHALL return HTTP 404 when the uid is unknown.

#### Scenario: Unauthenticated request to a data endpoint is rejected

- **WHEN** `GET /api/status` (or `GET /api/accounts`, or `POST /api/accounts/{email}/sync`) is called with no `X-Api-Key` header
- **THEN** the response is HTTP 401 and no account data is returned and no sync is triggered

#### Scenario: Valid key admits the request

- **WHEN** any protected `/api/*` endpoint is called with a correct `X-Api-Key`
- **THEN** the request is handled normally

#### Scenario: OAuth callback is reachable without the app API key

- **WHEN** `GET /api/auth/google/callback` is called with no `X-Api-Key` header (as Google's redirect always is)
- **THEN** the request is not rejected with 401 by the API-key middleware and reaches the callback handler, which then enforces OAuth `state`/PKCE validation

#### Scenario: OAuth login is reachable without the app API key

- **WHEN** `GET /api/auth/google/login` is called with no `X-Api-Key` header (a top-level browser navigation)
- **THEN** the request is not rejected with 401 by the API-key middleware and reaches the login handler, which redirects to Google's consent screen

#### Scenario: Other /api/auth paths stay default-deny

- **WHEN** any `/api/*` path other than the two exempt OAuth entry points — including a hypothetical `/api/auth/other` — is called with no `X-Api-Key` header
- **THEN** the response is HTTP 401 before any handler logic runs

#### Scenario: Auto-generated key is persisted and not logged

- **WHEN** the server starts with `DMAIL_API_KEY` unset and no persisted key file present
- **THEN** it generates a key, writes it to `DMAIL_DATA_DIR/keys/` with `0600` permissions, logs only that path, and the key value appears in no log output

#### Scenario: Persisted key is stable across restarts

- **WHEN** the server is restarted with `DMAIL_API_KEY` still unset and a key file already present under `DMAIL_DATA_DIR/keys/`
- **THEN** it reuses the persisted key rather than generating a new one

#### Scenario: Unknown uid returns 404

- **WHEN** `GET /api/emails/{uid}` is called (with a valid key) for a uid that does not exist
- **THEN** the response is HTTP 404

## ADDED Requirements

### Requirement: OAuth redirect URI is derived from configuration, not the request Host header

The server SHALL build the Google OAuth2 `redirect_uri` from a configured base URL, **never** from the inbound request `Host` header. The base URL SHALL be read from `DMAIL_OAUTH_REDIRECT_BASE_URL` (scheme, host, and optional port; no path); the effective `redirect_uri` SHALL be `{base}/api/auth/google/callback`. When `DMAIL_OAUTH_REDIRECT_BASE_URL` is unset, the server SHALL derive the base from its own known loopback bind (`http://127.0.0.1:{DMAIL_PORT}`) and SHALL NOT fall back to the request `Host`. The login handler and the callback handler SHALL compute the **identical** `redirect_uri`, so the value sent to Google's authorization endpoint and the value sent in the token exchange always match.

#### Scenario: Redirect URI comes from the configured base URL

- **WHEN** `DMAIL_OAUTH_REDIRECT_BASE_URL=https://dmail.example.internal` is set and `GET /api/auth/google/login` is invoked
- **THEN** the `redirect_uri` sent to Google is `https://dmail.example.internal/api/auth/google/callback` regardless of the request `Host` header

#### Scenario: A spoofed Host header does not influence the redirect URI

- **WHEN** a request to the login or callback endpoint arrives with a `Host` header (e.g. `Host: attacker.example`) that differs from the configured base URL
- **THEN** the computed `redirect_uri` is unaffected by the `Host` header and reflects only the configured (or loopback-default) base URL

#### Scenario: Loopback default when unconfigured

- **WHEN** `DMAIL_OAUTH_REDIRECT_BASE_URL` is unset and `DMAIL_PORT` is `8080`
- **THEN** the computed `redirect_uri` is `http://127.0.0.1:8080/api/auth/google/callback` and never derived from the request `Host`

### Requirement: OAuth callback validates state and PKCE before exchanging the authorization code

Because `GET /api/auth/google/callback` is exempt from the app API key, its security boundary SHALL be the OAuth `state` + PKCE handshake. The callback SHALL look up the PKCE `code_verifier` bound to the presented `state`, SHALL reject the request with HTTP 400 `invalid_state` when no verifier is stored for that `state`, and SHALL NOT exchange the authorization `code` (nor persist any tokens) unless a matching verifier is found. The `state` lookup SHALL be single-use (a given `state` cannot be validated twice).

#### Scenario: Missing state is rejected and no code exchange occurs

- **WHEN** `GET /api/auth/google/callback` is called with a `state` that has no stored PKCE verifier
- **THEN** the response is HTTP 400 `invalid_state` and the authorization code is not exchanged and no tokens are persisted

#### Scenario: Valid state with its bound verifier completes the exchange

- **WHEN** `GET /api/auth/google/callback` is called with a `state` whose PKCE verifier was stored by a prior `GET /api/auth/google/login`
- **THEN** the callback exchanges the authorization code using that bound verifier and the configured `redirect_uri`

#### Scenario: A state cannot be replayed

- **WHEN** the same `state` value is presented to the callback a second time after a successful first validation
- **THEN** the second request is rejected with HTTP 400 `invalid_state` because the stored verifier was consumed
