# DEVLOG — services-security-lockdown

Running log of implementation decisions and deviations, per block. Newest block at the bottom. The architect is spawned fresh each block, so this is its cross-block memory.

## Pinned facts (read before planning any block)

- **Bind policy is a LOCAL copy, not a reference.** `services/Dmail/BindAddressPolicy.cs` is an `internal static` copy of the `frontends/Dmon.Network/NetworkBindPolicy.cs` rule (ADR-028 forbids a `services/ → frontends/` reference; the source is `internal` anyway). When the Dcal block (Group 4) lands it will be the **second consumer** of a near-identical rule — the reviewer flagged "promote to a shared `core/` package if a third consumer appears." For now Dcal does NOT bind wildcard (default Kestrel bind), so Group 4 needs only the auth+constant-time work, not a bind guard — do not add a bind policy to Dcal unless a task calls for it.
- **`Uri.TryCreate` cannot parse Kestrel wildcard hosts.** `Uri.TryCreate("http://+:8080", …)` returns false, so the bind policy uses a manual authority parser (`TryExtractHost`) instead of `System.Uri`. Any future host-parsing work in these services should reuse `BindAddressPolicy.TryExtractHost`, not `System.Uri`. Verified fail-safe: non-parseable/non-loopback hosts fall into the reject branch, so nothing Kestrel would bind to all interfaces is ever classified loopback.
- **`Dmail.csproj` has `InternalsVisibleTo("Dmail.Tests")`** — test `internal` helpers directly; do NOT boot `WebApplicationFactory` in unit tests (the app needs the ONNX model + SQLite at startup). For Group 3/5.2 auth tests, the reviewer/worker will need a strategy that avoids a full boot OR accepts an integration test that provides the model+db — flag this when planning Group 3.
- **`docker-compose.yml` disambiguation:** the ROOT `./docker-compose.yml` is the Aspire dashboard (unrelated). The Dmail one is `services/Dmail/docker-compose.yml`.
- **AUDIT.md** at repo root is untracked and deliberately NOT part of any block commit (it's the repo-wide audit that motivated the change). Stage only block files.
- **Gates:** `make build` (TreatWarningsAsErrors), `env -u MEKO_API_KEY make test` (the `env -u` avoids the live-Meko smoke hang), `openspec validate services-security-lockdown --strict`.

## Block 1 — Dmail loopback-by-default bind (tasks 1.1–1.4, 5.1) — DONE, committed

- Added `services/Dmail/BindAddressPolicy.cs` (`Validate` + `Resolve`, mirrors NetworkBindPolicy; manual `TryExtractHost` — see pinned facts for why not `Uri`). `Resolve(null,"8080",false) → http://127.0.0.1:8080`; wildcard without opt-in throws `InvalidOperationException`; wildcard with `DMAIL_ALLOW_NONLOOPBACK=true` accepted.
- `Program.cs` resolves `DMAIL_BIND_ADDRESS`/`DMAIL_ALLOW_NONLOOPBACK` from config and validates BEFORE `UseUrls`/`builder.Build()` — fail-fast, no silent fallback.
- Dockerfile sets `DMAIL_ALLOW_NONLOOPBACK=true` + `DMAIL_BIND_ADDRESS=http://+:8080` (container namespace = boundary). `services/Dmail/docker-compose.yml` publishes `127.0.0.1:${DMAIL_PORT:-8080}:8080`. New `docs/deploying-dmail.md` (minimal bind note only — full deploy-doc/auth pass is deferred to task 6.2).
- Tests: `test/Dmail.Tests/BindAddressPolicyTests.cs`, 19 cases total (12 initial + 7 reviewer-requested regression cases: no-port wildcard/loopback, userinfo `user@+`, uppercase LOCALHOST, DNS hostname both ways, IPv6 wildcard with trailing path). Dmail.Tests 73/73.
- **Reviewer verdict:** approve, no blockers; parser hand-traced fail-safe on every wildcard/loopback case. Nits (regression tests + a triple-slice cleanup) were folded in before commit. Interesting: the userinfo case `http://user@+:8080` extracts host `user@+` (not bare `+`) and is rejected via the "not loopback" branch rather than the "wildcard" branch — still fail-safe.
- No parser bug found. No ADR contradiction.
