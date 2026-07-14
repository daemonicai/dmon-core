# Dmail server

A standalone ASP.NET Core service that ingests mail over IMAP, embeds it with a
bundled BERT ONNX model, and answers hybrid (vector + FTS) search queries. It is
the backing server for the [`tools/Dmon.Tools.Dmail`](../../tools/Dmon.Tools.Dmail)
agent tool, which reaches it **only over HTTP** — there is no in-process link
between the two. It is an **app artifact**, versioned and deployed on its own
schedule (not on the protocol-lockstep NuGet train; `IsPackable=false`).

## Configuration

All configuration is via environment variables:

| Variable | Default | Purpose |
|----------|---------|---------|
| `DMAIL_DATA_DIR` | `/data` | Directory for the SQLite database and Data-Protection key ring. |
| `DMAIL_PORT` | `8080` | HTTP listen port. |
| `DMAIL_API_KEY` | _(auto-generated)_ | Required as `X-Api-Key` on every `/api/*` endpoint. If unset, a key is auto-generated on first run and written to `<DMAIL_DATA_DIR>/keys/api-key` (owner-only permissions), reused across restarts. Only the file path is logged at startup — never the key value. |
| `DMAIL_OAUTH_REDIRECT_BASE_URL` | _(loopback)_ | Origin (scheme + host + optional port, no path) used to build the Google OAuth2 `redirect_uri` (`{base}/api/auth/google/callback`). Derived from the request `Host` is **never** used. When unset, falls back to the server's own loopback bind `http://127.0.0.1:{DMAIL_PORT}`. Set this to the externally-reachable base URL for containerised or non-loopback deployments; it must be the exact value registered in the Google Cloud console. |
| `DMAIL_BACKFILL_MONTHS` | `1` | How many months of history to backfill per account. |
| `DMAIL_GOOGLE_CLIENT_ID` / `DMAIL_GOOGLE_CLIENT_SECRET` | _(none)_ | Google OAuth2 client credentials, required to link Gmail accounts. |

See [`.env.example`](./.env.example) for the Docker Compose form.

## HTTP surface

Every endpoint under `/api` requires an `X-Api-Key` header (default-deny — see
[`docs/deploying-dmail.md`](../../docs/deploying-dmail.md) for the auth model
and key persistence). Missing or invalid keys get a `401` before the handler
runs.

- Agent endpoints (require `X-Api-Key`): `POST /api/search`, `POST /api/emails/list`, `GET /api/emails/{uid}`.
- Account/admin (require `X-Api-Key`): `GET /api/status`, `GET /api/accounts`, `DELETE /api/accounts/{email}`, `POST /api/accounts/{email}/sync`.
- OAuth2 (require `X-Api-Key`): `GET /api/auth/google/login`, `GET /api/auth/google/callback`.
- Open (no key required): `GET /health` (readiness), `GET /` and static assets under `/js/...` (admin dashboard from `wwwroot/`).

## Running

Locally:

```sh
dotnet run --project services/Dmail
```

With Docker — the build context is the **repository root** so central package
management resolves:

```sh
# from the repo root
docker build -f services/Dmail/Dockerfile -t dmail .

# or, from this directory, via Compose (context is ../..)
docker compose up --build
```
