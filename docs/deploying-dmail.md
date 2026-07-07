# Deploying Dmail

`services/Dmail` is a standalone ASP.NET Core backing server (ADR-028). This note
covers its network bind posture. See `services/Dmail/README.md` for the full
configuration reference.

## Bind policy: loopback by default

Dmail resolves its HTTP bind address at startup as follows:

- If `DMAIL_BIND_ADDRESS` is set, it is used verbatim (after validation).
- Otherwise it defaults to `http://127.0.0.1:{DMAIL_PORT}` — loopback only.

A wildcard/all-interfaces bind (`0.0.0.0`, `::`, `*`, `+`) or any other
non-loopback address is rejected at startup **unless** `DMAIL_ALLOW_NONLOOPBACK=true`
is set. Rejection throws a fatal, actionable error naming the offending address
and the fix, before the server starts listening.

Running `dotnet run --project services/Dmail` directly is therefore safe by
default: it binds `127.0.0.1` and is reachable only from the local machine.

## Docker

The container's network namespace is the security boundary, not the in-process
bind address: `services/Dmail/Dockerfile` sets `DMAIL_ALLOW_NONLOOPBACK=true`
and `DMAIL_BIND_ADDRESS=http://+:8080` so the process binds all interfaces
*inside* the container. `services/Dmail/docker-compose.yml` then publishes that
port to host loopback only (`127.0.0.1:${DMAIL_PORT}:8080`), not all host
interfaces — so the effective exposure on the host is still loopback-only. To
reach Dmail from another machine, front it with `tailscale serve` (as with the
core network host) rather than publishing to a non-loopback host address.

## Authentication (default-deny)

Every request under the `/api` prefix requires an `X-Api-Key` header. This is
default-deny: there is no way to disable it. A missing or invalid key gets a
`401` with body `{"error":"unauthorized"}`, before any endpoint handler runs.
This now covers **every** `/api/*` route, including the previously-open
account/admin endpoints (`GET /api/status`, `GET /api/accounts`,
`DELETE /api/accounts/{email}`, `POST /api/accounts/{email}/sync`) and the
OAuth endpoints (`GET /api/auth/google/login`, `GET /api/auth/google/callback`).

`GET /health` and the static admin dashboard (`GET /`, `/js/...`) are not under
`/api` and remain open — they carry no account data.

## API key persistence

Set `DMAIL_API_KEY` to pin the key explicitly. If it is unset, Dmail
auto-generates a key on first startup and persists it to
`<DMAIL_DATA_DIR>/keys/api-key` (owner-only permissions, mode `0600`), reusing
the same key across restarts. `DMAIL_DATA_DIR` defaults to `/data`.

The key value is **never logged** — only the file path is, at startup. Read
the file directly (e.g. `cat /data/keys/api-key` inside the container) to
retrieve it.

## OAuth (Google account linking)

The Google-account-linking browser flow (`/api/auth/google/login` and
`/api/auth/google/callback`) is now behind the same `X-Api-Key` gate as every
other `/api/*` route, so it must be reached with an authenticated admin
client (e.g. the dashboard, which sends the key from its own session, or a
manual request carrying `X-Api-Key`).

Host-header hardening on the OAuth callback (the redirect URI is built from
the incoming request's `Host` header) is a known, deferred follow-up — it is
not addressed by this change.
