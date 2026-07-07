## Context

`services/Dmail` and `services/Dcal` are standalone ASP.NET Core backing servers (ADR-028), configured entirely from `DMAIL_*` / `DCAL_*` env vars, deployed independently of the NuGet train. Each pairs with an in-process `tools/` extension (`Dmon.Tools.Dmail`, `Dmon.Tools.Dcal`) that reaches it over HTTP with an optional `X-Api-Key`. The intended deployment is a single-tenant home server behind Tailscale; the core's own network host (`frontends/Dmon.Network`) already enforces loopback-by-default via `NetworkBindPolicy` and a fail-closed per-device key set.

Current state (from `AUDIT.md`):
- **Dmail** binds `http://+:{port}` (wildcard), leaves `/api/status`, `/api/accounts`, `/api/accounts/{email}/sync`, and both OAuth endpoints unauthenticated (only `/api/search`, `/api/emails/list`, `/api/emails/{uid}` call `.RequireApiKey()`), and logs its auto-generated key in plaintext. Key *validation* is already constant-time (`CryptographicOperations.FixedTimeEquals`).
- **Dmail** runs in Docker (`services/Dmail/Dockerfile`, `EXPOSE 8080`, `HEALTHCHECK` curls `http://localhost:8080/health`).
- **Dcal** installs its `X-Api-Key` middleware only inside `if (!string.IsNullOrEmpty(apiKey))`, so an unconfigured server is fully open; it does **not** bind wildcard (default Kestrel bind), and its key check is `key != apiKey` (not constant-time).

## Goals / Non-Goals

**Goals:**
- Dmail binds loopback by default; wildcard/all-interfaces only via an explicit `DMAIL_ALLOW_NONLOOPBACK=true` opt-in, with a fail-fast, actionable rejection otherwise — mirroring `NetworkBindPolicy`.
- Every Dmail and Dcal `/api/*` endpoint requires a valid `X-Api-Key` (default-deny); no endpoint is open merely because a key was not configured.
- No API key is ever written to a log. Auto-generated keys are persisted to a `chmod 600` file and only the path is logged.
- `GET /health` on both servers stays unauthenticated (Docker probes depend on it) and carries no account list or connection-count detail.
- Dcal uses a constant-time key comparison.

**Non-Goals:**
- No change to the wire contract between the tools and the servers (still `X-Api-Key` header; base URLs unchanged).
- No pairing/QR/device-key model for the services (that is the core network host's concern, ADR-018); a single shared `X-Api-Key` per service is retained.
- No TLS termination in-process (Tailscale / reverse proxy remains the transport boundary).
- Not extracting a shared bind-policy library across `frontends/` and `services/` — `NetworkBindPolicy` is `internal` to `Dmon.Network`; Dmail gets a small local guard rather than a new cross-bucket dependency (a `services/ → frontends/` reference would be a layering violation).

## Decisions

### D1 — Dmail bind: loopback default, explicit non-loopback opt-in
Replace `UseUrls("http://+:{port}")` with a resolved bind address:
- If `DMAIL_BIND_ADDRESS` is set, use it verbatim (validated below).
- Else default to `http://127.0.0.1:{DMAIL_PORT}`.
Validate with a local helper mirroring `NetworkBindPolicy.Validate`: loopback always allowed; wildcard (`0.0.0.0`, `::`, `*`, `+`) allowed **only** when `DMAIL_ALLOW_NONLOOPBACK=true`; other specific hosts allowed only under the same opt-in. On rejection, throw at startup with a message naming the offending address and how to fix it. The `Dockerfile` sets `DMAIL_ALLOW_NONLOOPBACK=true` and `DMAIL_BIND_ADDRESS=http://+:8080` (a container's namespace is the boundary); `docker-compose.yml`/deploy docs publish the port to `127.0.0.1` on the host.

*Rationale:* a bare `dotnet run` is safe by default; the container path keeps working with an explicit, auditable opt-in. Reusing the core's exact rule (wildcard always forbidden unless opt-in) keeps operator mental model consistent.

*Alternative rejected:* referencing `Dmon.Network.NetworkBindPolicy` directly — it is `internal` and lives in a `frontends/` project; a `services/ → frontends/` ProjectReference inverts the layering. A ~30-line local copy is the lesser evil; if a third consumer appears, promote the policy into `core/` then.

### D2 — Dmail default-deny auth on all `/api/*`
Add `.RequireApiKey()` to every currently-unprotected mapping: `/api/status`, `/api/accounts` (GET), `/api/accounts/{email}` (DELETE), `/api/accounts/{email}/sync` (POST), `/api/auth/google/login`, `/api/auth/google/callback`. `/health` is **not** protected. To make "default-deny" structural rather than per-endpoint discipline, prefer a route-group / global filter that requires the key for the whole `/api` prefix, with `/health` (and static dashboard files) explicitly outside it — so a future endpoint is protected by construction, not by remembering to append `.RequireApiKey()`.

*OAuth note:* the login/callback endpoints are browser-driven (the operator visits them to authorise a mailbox). Requiring `X-Api-Key` means the operator drives OAuth through an authenticated client, not a bare browser. This is acceptable for a single-tenant admin flow; document it. The callback's `redirect_uri` continues to derive from the request Host header — Host-header hardening is noted as a residual (out of scope here; state/PKCE already mitigate CSRF).

### D3 — Dmail: persist auto-generated key, never log it
In `ApiKeyService`, when `DMAIL_API_KEY` is unset:
- Path `DMAIL_DATA_DIR/keys/api-key`. If it exists, read and reuse it (stable across restarts). If not, generate, write with `0600` perms (`File.WriteAllText` + `File.SetUnixFileMode(..., UserRead|UserWrite)`), and reuse.
- Log only: `"Auto-generated API key written to {Path}. Set DMAIL_API_KEY to override."` — never the value.

*Rationale:* today the key changes every restart (in-memory) and is leaked to logs. Persisting makes it stable (the paired tool can be pointed at it) and removes the secret from log sinks. The `keys/` dir already exists (data-protection keys live there) and is under `DMAIL_DATA_DIR`.

### D4 — Dcal: unconditional middleware + constant-time compare + persist
Remove the `if (!string.IsNullOrEmpty(apiKey))` guard so the `X-Api-Key` middleware is **always** installed. Resolve the key the same way as Dmail: `DCAL_API_KEY` if set, else auto-generate-and-persist to `<data>/keys/api-key` (Dcal currently writes `calendar.db` in the CWD — introduce a `DCAL_DATA_DIR` default or place the key next to the db; decided in tasks). Replace `key != apiKey` with `CryptographicOperations.FixedTimeEquals` over UTF-8 bytes (lift the same helper Dmail uses). `/health` stays exempt (already is).

*Rationale:* closes the "unconfigured = open" gap and the timing side-channel in one pass, matching Dmail's posture so the two services behave identically.

### D5 — `/health` body trim
Dmail `/health` currently returns `idle_connections` (a count) alongside `status`/`model_loaded`/`database_ok`. Drop `idle_connections` from the unauthenticated body; keep it available on the authenticated `/api/status`. Dcal `/health` returns `lastSync`/`eventCount` — mildly informative but not account data; keep as-is (a count and a timestamp are acceptable liveness signal), but confirm no account identifiers leak.

## Risks / Trade-offs

- **Deployment friction (accepted).** Operators running the servers bare-metal on a non-loopback address must now set `DMAIL_ALLOW_NONLOOPBACK=true` (and Dcal callers relying on unconfigured-open access must now supply a key). This is the intended behaviour change; the fail-fast messages tell them exactly what to set. Mitigated by the Dockerfile shipping the opt-in and docs covering host-loopback publishing.
- **OAuth-behind-auth (accepted).** Gating `/api/auth/google/*` behind `X-Api-Key` complicates the browser authorise flow. For a single admin operator this is acceptable; documented. If it proves unworkable in practice, a scoped exception for the login redirect could be revisited in a follow-up — not pre-emptively carved out here (default-deny first).
- **Key persistence location.** Reusing a persisted key across restarts is a behaviour change from the current per-restart in-memory key; this is strictly better (stable + unlogged) but means the file must be protected — `0600` + it lives under the already-sensitive `DMAIL_DATA_DIR/keys` (same dir as data-protection master keys), so the trust boundary is unchanged.
- **Local bind-policy duplication.** A ~30-line copy of the wildcard/loopback rule in Dmail can drift from `NetworkBindPolicy`. Low risk (the rule is stable and simple); a code comment cross-references the source of truth, and promotion to `core/` is the documented path if a third consumer appears.
