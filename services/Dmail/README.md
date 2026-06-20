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
| `DMAIL_API_KEY` | _(auto-generated)_ | Required on the agent endpoints as `X-Api-Key`. If unset, a key is generated on first run and logged once. |
| `DMAIL_BACKFILL_MONTHS` | `1` | How many months of history to backfill per account. |
| `DMAIL_GOOGLE_CLIENT_ID` / `DMAIL_GOOGLE_CLIENT_SECRET` | _(none)_ | Google OAuth2 client credentials, required to link Gmail accounts. |

See [`.env.example`](./.env.example) for the Docker Compose form.

## HTTP surface

- Agent endpoints (require `X-Api-Key`): `POST /api/search`, `POST /api/emails/list`, `GET /api/emails/{uid}`.
- Account/admin (unauthenticated): `GET /api/status`, `GET /api/accounts`, `DELETE /api/accounts/{email}`, `POST /api/accounts/{email}/sync`.
- OAuth2: `GET /api/auth/google/login`, `GET /api/auth/google/callback`.
- `GET /health` (readiness), `GET /` (admin dashboard from `wwwroot/`).

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
