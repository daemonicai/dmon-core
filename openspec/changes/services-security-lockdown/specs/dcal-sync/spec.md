## ADDED Requirements

### Requirement: Dcal HTTP API requires an API key by default

The `Dcal` server SHALL require a valid API key (`X-Api-Key`) on every `/api/*` endpoint (`GET /api/events/next`, `GET /api/events/upcoming`, `POST /api/sync`) regardless of whether `DCAL_API_KEY` is configured — the authentication middleware SHALL be installed unconditionally (default-deny), never gated on the presence of a configured key. A request to any `/api/*` endpoint with a missing or incorrect key SHALL be rejected with HTTP 401 before any handler logic runs. The `GET /health` endpoint SHALL remain exempt so container and reverse-proxy liveness probes work without the secret. The key SHALL be validated with a constant-time comparison.

When `DCAL_API_KEY` is unset, the server SHALL auto-generate a key on first run, persist it to a file with owner-only (`0600`) permissions, reuse that persisted key on subsequent restarts, and log only the file **path** — never the key value.

#### Scenario: Unauthenticated request is rejected even when no key is configured

- **WHEN** `Dcal` runs with `DCAL_API_KEY` unset and `GET /api/events/upcoming` is called with no `X-Api-Key` header
- **THEN** the response is HTTP 401 (the server is not open merely because no key was configured)

#### Scenario: Valid key admits the request

- **WHEN** `GET /api/events/upcoming` (or `/api/events/next`, or `POST /api/sync`) is called with a correct `X-Api-Key`
- **THEN** the request is handled normally

#### Scenario: Health requires no API key

- **WHEN** `GET /health` is called with no `X-Api-Key` header
- **THEN** the request is served (not rejected with 401)

#### Scenario: Incorrect key is rejected in constant time

- **WHEN** `GET /api/events/next` is called with an incorrect `X-Api-Key`
- **THEN** the response is HTTP 401 and the comparison uses a constant-time equality check (no early-out on the first differing byte)

#### Scenario: Auto-generated key is persisted and not logged

- **WHEN** `Dcal` starts with `DCAL_API_KEY` unset and no persisted key file present
- **THEN** it generates a key, writes it with `0600` permissions, logs only that path, and the key value appears in no log output
