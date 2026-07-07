# Deploying Dcal

`services/Dcal` is a standalone ASP.NET Core backing server (ADR-028) for the
[`tools/Dmon.Tools.Dcal`](../tools/Dmon.Tools.Dcal) agent tool.

## Authentication (default-deny)

Every route except `GET /health` requires an `X-Api-Key` header — this is
default-deny and unconditional, regardless of whether the key is configured or
auto-generated. A missing or invalid key gets a `401`, before the route
handler runs. The comparison is constant-time.

Set `DCAL_API_KEY` to pin the key explicitly. If it is unset, Dcal
auto-generates one on first startup.

## API key persistence

An auto-generated key is persisted to `<DCAL_DATA_DIR>/keys/api-key`
(owner-only permissions, mode `0600`) and reused across restarts. Only the
file path is logged at startup — never the key value.

`DCAL_DATA_DIR` defaults to `"."` (Dcal is bare-metal-first, unlike Dmail's
Docker-first `/data` default), so by default the key lives at
`./keys/api-key`, next to `calendar.db` in the working directory.

Dcal has no network bind guard — unlike Dmail (see
[`docs/deploying-dmail.md`](./deploying-dmail.md)), it does not restrict or
validate the interface it listens on.
