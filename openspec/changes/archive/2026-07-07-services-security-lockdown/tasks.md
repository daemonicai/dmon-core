## 1. Dmail — loopback-by-default bind (D1)

- [x] 1.1 Add a local bind-policy guard to `services/Dmail` (a `BindAddressPolicy` helper, ~30 lines) mirroring `Dmon.Network.NetworkBindPolicy.Validate`: loopback always allowed; wildcard (`0.0.0.0`/`::`/`*`/`+`) and other non-loopback allowed only when the opt-in is true; returns `(bool allowed, string? error)`. Add a code comment cross-referencing `frontends/Dmon.Network/NetworkBindPolicy.cs` as the source of truth.
- [x] 1.2 In `services/Dmail/Program.cs`, resolve the bind address: use `DMAIL_BIND_ADDRESS` if set, else `http://127.0.0.1:{DMAIL_PORT}`; read `DMAIL_ALLOW_NONLOOPBACK` (default false); validate via 1.1 and throw a fatal, actionable exception on rejection; pass the validated address to `UseUrls` (replacing `http://+:{port}`).
- [x] 1.3 Update `services/Dmail/Dockerfile` to set `ENV DMAIL_ALLOW_NONLOOPBACK=true` and `ENV DMAIL_BIND_ADDRESS=http://+:8080` (container namespace is the boundary); keep `EXPOSE 8080` and the existing `/health` `HEALTHCHECK`.
- [x] 1.4 Ensure `docker-compose.yml` / deploy docs publish the Dmail port to host loopback (`127.0.0.1:8080:8080`) rather than all host interfaces; add a short note in `docs/` on the loopback-default + opt-in model.

## 2. Dmail — persist auto-generated key, never log it (D3)

- [x] 2.1 In `services/Dmail/Services/ApiKeyService.cs`, when `DMAIL_API_KEY` is unset: resolve `DMAIL_DATA_DIR/keys/api-key`; if present, read and reuse; if absent, generate (existing `GenerateApiKey`), write with owner-only perms (`File.WriteAllText` + `File.SetUnixFileMode(path, UserRead | UserWrite)`), and reuse.
- [x] 2.2 Replace the two `LogWarning("Auto-generated API key: {Key}", ...)` / follow-up lines with a single log line that names the **path** only and instructs the operator to set `DMAIL_API_KEY`. Confirm the key value appears in no log statement anywhere in the project.

## 3. Dmail — default-deny auth on all `/api/*` (D2)

- [x] 3.1 Make API-key enforcement structural for the `/api` prefix: apply the `RequireApiKey` filter to the whole `/api` route group (or a global filter keyed on `PathStartsWithSegments("/api")`), so new endpoints are protected by construction. Keep `GET /health` and static dashboard files outside it.
- [x] 3.2 Verify every existing `/api/*` mapping in `services/Dmail/EndpointExtensions.cs` is now covered (`/api/status`, `/api/accounts` GET, `/api/accounts/{email}` DELETE, `/api/accounts/{email}/sync` POST, `/api/auth/google/login`, `/api/auth/google/callback`, plus the already-protected search/list/get) and remove now-redundant per-endpoint `.RequireApiKey()` calls if the group filter subsumes them. Ensure a rejected request returns 401 before handler logic runs.
- [x] 3.3 Trim the `GET /health` body (D5): drop `idle_connections`; keep `status`/`model_loaded`/`database_ok` only. Confirm `/api/status` (authenticated) still carries the connection-count detail for operators.

## 4. Dcal — default-deny auth + constant-time compare (D4)

- [x] 4.1 In `services/Dcal/Program.cs`, remove the `if (!string.IsNullOrEmpty(apiKey))` guard so the `X-Api-Key` middleware is always installed; keep the `/health` exemption.
- [x] 4.2 Resolve the Dcal key like Dmail: `DCAL_API_KEY` if set, else auto-generate-and-persist to a `0600` file (introduce a `DCAL_DATA_DIR` default, or place `keys/api-key` next to `calendar.db`; pick one and note it), logging only the path.
- [x] 4.3 Replace the `key != apiKey` check with a constant-time comparison (`CryptographicOperations.FixedTimeEquals` over UTF-8 bytes), matching Dmail's `ApiKeyService.Validate`.

## 5. Tests

- [x] 5.1 `test/Dmail.Tests`: bind-policy cases — loopback default resolves to `127.0.0.1`; wildcard without opt-in throws at startup; wildcard with `DMAIL_ALLOW_NONLOOPBACK=true` is accepted.
- [x] 5.2 `test/Dmail.Tests`: auth cases — `/api/status`, `/api/accounts`, `/api/accounts/{email}/sync`, and an OAuth endpoint each return 401 without a key and succeed (or reach handler) with a valid key; `/health` returns 200 with no key and its body contains no `idle_connections`/account fields.
- [x] 5.3 `test/Dmail.Tests`: key-persistence cases — auto-generate writes a `0600` file and logs only the path (assert the key string is absent from captured logs); a second construction reuses the persisted key.
- [x] 5.4 `test/Dcal.Tests`: auth cases — with `DCAL_API_KEY` unset, `/api/events/upcoming` returns 401 without a key; `/health` is open; valid key admits; constant-time comparison in use (behavioural: wrong key → 401).

## 6. Validate & docs

- [x] 6.1 `make build` clean (TreatWarningsAsErrors), `make test` green (`env -u MEKO_API_KEY` if needed), `openspec validate services-security-lockdown --strict`.
- [x] 6.2 Update `docs/` (deploy guidance) and any Dmail/Dcal README notes to document `DMAIL_ALLOW_NONLOOPBACK`, the loopback-default posture, default-deny auth, and the persisted-key file location. Note the residual OAuth-behind-auth admin flow and the deferred Host-header hardening for the OAuth callback.
