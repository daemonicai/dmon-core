## MODIFIED Requirements

### Requirement: Dmail server runs as a standalone service artifact

The `Dmail` server SHALL be a standalone ASP.NET Core (`Microsoft.NET.Sdk.Web`) application that lives under `services/Dmail/`, is **not** packed to NuGet (`IsPackable=false`), and is versioned and deployed independently of the protocol-lockstep NuGet train (ADR-024/028). It SHALL be configured entirely from environment variables — `DMAIL_DATA_DIR` (default `/data`), `DMAIL_PORT` (default `8080`), `DMAIL_BIND_ADDRESS` (optional; overrides the default loopback bind), `DMAIL_ALLOW_NONLOOPBACK` (default `false`), `DMAIL_API_KEY`, `DMAIL_BACKFILL_MONTHS` (default `1`), and the Google OAuth2 client credentials — and SHALL persist all state (SQLite database, data-protection keys, and any auto-generated API key) under `DMAIL_DATA_DIR`.

The server SHALL bind a **loopback** address by default (`http://127.0.0.1:{DMAIL_PORT}`) and SHALL reject a wildcard/all-interfaces bind (`0.0.0.0`, `::`, `*`, `+`) or any other non-loopback address unless `DMAIL_ALLOW_NONLOOPBACK` is `true`. When a bind address is forbidden, the server SHALL fail fast at startup with an actionable error message naming the offending address; it SHALL NOT silently fall back to a different address.

#### Scenario: Server binds loopback by default

- **WHEN** the server starts with `DMAIL_PORT` set and neither `DMAIL_BIND_ADDRESS` nor `DMAIL_ALLOW_NONLOOPBACK` set
- **THEN** it listens on `http://127.0.0.1:{DMAIL_PORT}` and creates/uses its SQLite database and key store under `DMAIL_DATA_DIR`

#### Scenario: Wildcard bind is rejected without the opt-in

- **WHEN** the server starts with a wildcard bind address (e.g. `DMAIL_BIND_ADDRESS=http://+:8080`) and `DMAIL_ALLOW_NONLOOPBACK` is not `true`
- **THEN** the process exits at startup with an error message naming the address and instructing the operator to bind loopback or set `DMAIL_ALLOW_NONLOOPBACK=true`

#### Scenario: Non-loopback bind allowed with explicit opt-in

- **WHEN** the server starts with `DMAIL_BIND_ADDRESS=http://+:8080` and `DMAIL_ALLOW_NONLOOPBACK=true` (the containerised deployment)
- **THEN** it binds the requested address

#### Scenario: Server is not a NuGet package

- **WHEN** `dotnet pack` runs over the repository
- **THEN** the `Dmail` server project produces no `.nupkg` (it is `IsPackable=false`)

### Requirement: Startup validation gates readiness

The server SHALL, on startup and before serving requests, initialise its SQLite database (WAL mode, schema, FTS5), ensure the vector (`vec0`) collection exists, and validate that the bundled ONNX embedding model (`bge-micro-v2.onnx` + `vocab.txt`) loads. The `GET /health` endpoint SHALL be an **unauthenticated** liveness probe (no `X-Api-Key` required, so container `HEALTHCHECK` and reverse-proxy probes work without embedding the secret) and SHALL report `healthy` only when the embedding model is loaded and the database is reachable, returning HTTP 503 with `degraded` status otherwise. The `/health` body SHALL contain only non-sensitive liveness fields (status, model-loaded, database-ok) and SHALL NOT include account identifiers or connection-count detail.

#### Scenario: Health reflects model and database readiness

- **WHEN** `GET /health` is called and both the ONNX model is loaded and the database responds
- **THEN** the response is HTTP 200 with `status: "healthy"`, `model_loaded: true`, `database_ok: true` and no account or connection-count fields

#### Scenario: Health degrades when the database is unreachable

- **WHEN** `GET /health` is called and the database cannot be queried
- **THEN** the response is HTTP 503 with `status: "degraded"`

#### Scenario: Health requires no API key

- **WHEN** `GET /health` is called with no `X-Api-Key` header
- **THEN** the request is served (not rejected with 401)

### Requirement: Agent-facing HTTP API requires an API key

The server SHALL require a valid API key (`X-Api-Key`) on **every** `/api/*` endpoint (default-deny), including — but not limited to — `POST /api/search`, `POST /api/emails/list`, `GET /api/emails/{uid}`, `GET /api/status`, `GET /api/accounts`, `DELETE /api/accounts/{email}`, `POST /api/accounts/{email}/sync`, `GET /api/auth/google/login`, and `GET /api/auth/google/callback`. A request to any `/api/*` endpoint with a missing or incorrect key SHALL be rejected with HTTP 401 before any handler logic runs. Only `GET /health` and the static dashboard files are exempt. The key SHALL be validated with a constant-time comparison.

When `DMAIL_API_KEY` is unset, the server SHALL auto-generate a key on first run, persist it to a file under `DMAIL_DATA_DIR/keys/` with owner-only (`0600`) permissions, reuse that persisted key on subsequent restarts, and log only the file **path** — never the key value.

The `GET /api/emails/{uid}` endpoint SHALL return the full message body, and SHALL return HTTP 404 when the uid is unknown.

#### Scenario: Unauthenticated request to a data endpoint is rejected

- **WHEN** `GET /api/status` (or `GET /api/accounts`, or `POST /api/accounts/{email}/sync`) is called with no `X-Api-Key` header
- **THEN** the response is HTTP 401 and no account data is returned and no sync is triggered

#### Scenario: Valid key admits the request

- **WHEN** any `/api/*` endpoint is called with a correct `X-Api-Key`
- **THEN** the request is handled normally

#### Scenario: Auto-generated key is persisted and not logged

- **WHEN** the server starts with `DMAIL_API_KEY` unset and no persisted key file present
- **THEN** it generates a key, writes it to `DMAIL_DATA_DIR/keys/` with `0600` permissions, logs only that path, and the key value appears in no log output

#### Scenario: Persisted key is stable across restarts

- **WHEN** the server is restarted with `DMAIL_API_KEY` still unset and a key file already present under `DMAIL_DATA_DIR/keys/`
- **THEN** it reuses the persisted key rather than generating a new one

#### Scenario: Unknown uid returns 404

- **WHEN** `GET /api/emails/{uid}` is called (with a valid key) for a uid that does not exist
- **THEN** the response is HTTP 404
