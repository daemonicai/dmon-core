## Why

The 2026-07-06 repo audit (`AUDIT.md`, issue 1) found the `services/Dmail` server is materially exposed:

- **Binds all interfaces.** `services/Dmail/Program.cs` calls `UseUrls("http://+:{port}")` — a wildcard bind that the core's own `NetworkBindPolicy` forbids unconditionally. A bare `dotnet run` on a workstation is reachable from the whole LAN/tailnet.
- **Unauthenticated agent-facing endpoints.** `GET /api/status`, `GET /api/accounts`, `POST /api/accounts/{email}/sync`, and the OAuth login/callback pair are mapped **without** `.RequireApiKey()`. Any host that can reach the port can enumerate the operator's mail accounts and trigger IMAP syncs with no credential.
- **Secret written to the log.** When `DMAIL_API_KEY` is unset, `ApiKeyService` auto-generates a key and writes it in plaintext via `_logger.LogWarning("Auto-generated API key: {Key}", ...)`, leaking it into stdout and any log sink.

The sibling `services/Dcal` server has a related weakness: its `X-Api-Key` middleware is installed **only when** `DCAL_API_KEY` is set (`services/Dcal/Program.cs`), so an unconfigured deployment serves every `/api/*` endpoint open, and its key check uses a non-constant-time `key != apiKey` comparison.

This is a single-tenant home-server product fronted by Tailscale, so the blast radius is bounded — but these are default-open, secret-leaking behaviours that defence-in-depth should close, and they contradict the loopback-by-default posture the core already enforces.

## What Changes

- **Dmail — loopback by default.** Bind `http://127.0.0.1:{DMAIL_PORT}` by default; reject wildcard/all-interfaces binds unless `DMAIL_ALLOW_NONLOOPBACK=true` is set (the Dockerfile sets it, since a container's namespace is the boundary and the operator publishes to host loopback). Fail fast with an actionable message on a forbidden bind. Mirrors the core `NetworkBindPolicy` contract.
- **Dmail — default-deny auth.** Require a valid `X-Api-Key` on **every** `/api/*` endpoint (`/api/status`, `/api/accounts`, `/api/accounts/{email}` DELETE, `/api/accounts/{email}/sync`, `/api/auth/google/login`, `/api/auth/google/callback`, in addition to the already-protected search/list/get). `GET /health` stays unauthenticated as a liveness probe (Docker `HEALTHCHECK` curls it) but its body is trimmed to non-sensitive fields only.
- **Dmail — stop logging the key.** On auto-generation, persist the key to a `chmod 600` file under `DMAIL_DATA_DIR/keys/` and log only its **path**, never the value. Log a one-line instruction to set `DMAIL_API_KEY`.
- **Dcal — default-deny auth.** Always install the `X-Api-Key` middleware (not conditionally on `DCAL_API_KEY`); when no key is configured, either auto-generate-and-persist (same pattern as Dmail) or fail fast — decided in design. Use a constant-time comparison.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `dmail-server`: the "Server binds the configured port and data directory" scenario changes from `http://+:{DMAIL_PORT}` to a loopback-default bind with an explicit non-loopback opt-in; the "Agent-facing HTTP API requires an API key" requirement broadens from three endpoints to **all** `/api/*` endpoints (default-deny) and its auto-generate-and-log behaviour changes to auto-generate-and-persist (no secret in logs); `/health` is specified as an unauthenticated liveness endpoint with a non-sensitive body.
- `dcal-lookup`: the agent-facing calendar HTTP API requirement is strengthened to require the API key unconditionally (default-deny, not opt-in) with a constant-time key comparison.

## Impact

- **Code:** `services/Dmail/Program.cs` (bind), `services/Dmail/Services/ApiKeyService.cs` (persist-not-log), `services/Dmail/EndpointExtensions.cs` (add `.RequireApiKey()` to unprotected endpoints; trim `/health` body), `services/Dmail/Dockerfile` + `docker-compose.yml` (set `DMAIL_ALLOW_NONLOOPBACK`, publish to host loopback); `services/Dcal/Program.cs` (unconditional middleware + constant-time compare).
- **Config surface:** new `DMAIL_ALLOW_NONLOOPBACK` (default `false`); `DMAIL_API_KEY` / `DCAL_API_KEY` semantics unchanged when set.
- **Deployment:** operators running Dmail/Dcal bare-metal now get a loopback bind by default; container deployments must set the opt-in (done in the shipped Dockerfile) and should publish the port to `127.0.0.1` on the host. Documented in `docs/`.
- **Tests:** `test/Dmail.Tests` and `test/Dcal.Tests` gain auth-coverage and bind-policy cases.
- **Specs:** `dmail-server`, `dcal-lookup` standing specs updated at archive time.
- **No ADR change:** this aligns the services with the existing loopback-by-default / API-key posture (ADR-012 bind policy; the ADR-006 permission model). No binding decision is contradicted.
