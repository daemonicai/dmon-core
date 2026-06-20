# dmail-server Specification

## Purpose
TBD - created by archiving change graft-dmail-server. Update Purpose after archive.
## Requirements
### Requirement: Dmail server runs as a standalone service artifact

The `Dmail` server SHALL be a standalone ASP.NET Core (`Microsoft.NET.Sdk.Web`) application that lives under `services/Dmail/`, is **not** packed to NuGet (`IsPackable=false`), and is versioned and deployed independently of the protocol-lockstep NuGet train (ADR-024/028). It SHALL be configured entirely from environment variables — `DMAIL_DATA_DIR` (default `/data`), `DMAIL_PORT` (default `8080`), `DMAIL_API_KEY`, `DMAIL_BACKFILL_MONTHS` (default `1`), and the Google OAuth2 client credentials — and SHALL persist all state (SQLite database, data-protection keys) under `DMAIL_DATA_DIR`.

#### Scenario: Server binds the configured port and data directory

- **WHEN** the server starts with `DMAIL_PORT` and `DMAIL_DATA_DIR` set
- **THEN** it listens on `http://+:{DMAIL_PORT}` and creates/uses its SQLite database and key store under `DMAIL_DATA_DIR`

#### Scenario: Server is not a NuGet package

- **WHEN** `dotnet pack` runs over the repository
- **THEN** the `Dmail` server project produces no `.nupkg` (it is `IsPackable=false`)

### Requirement: Startup validation gates readiness

The server SHALL, on startup and before serving requests, initialise its SQLite database (WAL mode, schema, FTS5), ensure the vector (`vec0`) collection exists, and validate that the bundled ONNX embedding model (`bge-micro-v2.onnx` + `vocab.txt`) loads. The `/health` endpoint SHALL report `healthy` only when the embedding model is loaded and the database is reachable, and SHALL return HTTP 503 with `degraded` status otherwise.

#### Scenario: Health reflects model and database readiness

- **WHEN** `GET /health` is called and both the ONNX model is loaded and the database responds
- **THEN** the response is HTTP 200 with `status: "healthy"`, `model_loaded: true`, `database_ok: true`

#### Scenario: Health degrades when the database is unreachable

- **WHEN** `GET /health` is called and the database cannot be queried
- **THEN** the response is HTTP 503 with `status: "degraded"`

### Requirement: IMAP ingestion of subscribed mailboxes

The server SHALL ingest mail from configured accounts over IMAP using an IDLE watcher per account, backfilling history for `DMAIL_BACKFILL_MONTHS` months and then watching for new messages, and SHALL track per-account sync state (`account_state`, `last_sync`, `indexed_email_count`, backfill status). Ingested messages SHALL be queued for processing through a bounded channel and persisted to the `data_emails` store.

#### Scenario: Account status is reported

- **WHEN** `GET /api/status` is called
- **THEN** the response lists each account with its state, last sync, indexed count, backfill status, and the active IMAP idle-connection count

#### Scenario: Removing an account stops its watcher and purges its mail

- **WHEN** `DELETE /api/accounts/{email}` is called
- **THEN** the account's IMAP watcher is stopped and its rows are removed from `accounts`, `data_emails`, and `backfill_state`

### Requirement: ONNX embedding and hybrid search

The server SHALL embed message text with the bundled BERT ONNX model and store embeddings in a SqliteVec vector collection sharing the SQLite file, and SHALL answer `POST /api/search` with a hybrid result that fuses vector similarity and SQLite FTS5 full-text matches via reciprocal-rank fusion.

#### Scenario: Search returns fused results

- **WHEN** an authenticated `POST /api/search` request is made with a query
- **THEN** the server returns results ranked by reciprocal-rank fusion of vector and full-text matches

### Requirement: Agent-facing HTTP API requires an API key

The server SHALL expose the agent-facing endpoints consumed by `tools/Dmon.Tools.Dmail` — `POST /api/search`, `POST /api/emails/list`, and `GET /api/emails/{uid}` — and SHALL require a valid API key (`X-Api-Key`) on each. When `DMAIL_API_KEY` is unset the server SHALL auto-generate a key on first run and log it once. The `GET /api/emails/{uid}` endpoint SHALL return the full message body, and SHALL return HTTP 404 when the uid is unknown.

#### Scenario: Agent endpoints reject a missing or wrong API key

- **WHEN** an agent endpoint is called without a valid `X-Api-Key`
- **THEN** the request is rejected as unauthorized

#### Scenario: Full email retrieval returns the body or 404

- **WHEN** an authenticated `GET /api/emails/{uid}` request is made
- **THEN** the server returns the full email (uid, account, subject, body, from, date, labels), or HTTP 404 when no email has that uid

### Requirement: OAuth2 account linking with encrypted token storage

The server SHALL support linking Gmail accounts via Google OAuth2 (`GET /api/auth/google/login` → consent → `GET /api/auth/google/callback`) using PKCE with server-side state/verifier storage, and SHALL encrypt persisted OAuth tokens at rest using ASP.NET Core Data Protection with keys persisted under `{DMAIL_DATA_DIR}/keys`. An invalid OAuth state SHALL be rejected.

#### Scenario: Invalid OAuth state is rejected

- **WHEN** `GET /api/auth/google/callback` is called with a `state` that has no stored verifier
- **THEN** the server responds with HTTP 400 `invalid_state` and does not exchange the code

### Requirement: Admin dashboard

The server SHALL serve a static admin dashboard from `wwwroot/` (default file `index.html`) that surfaces account status and management, backed by the unauthenticated status/account-listing endpoints (`GET /api/status`, `GET /api/accounts`).

#### Scenario: Dashboard is served at the root

- **WHEN** `GET /` is requested
- **THEN** the server returns the `wwwroot/index.html` dashboard

