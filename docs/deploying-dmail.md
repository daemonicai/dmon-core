# Deploying Dmail

`services/Dmail` is a standalone ASP.NET Core backing server (ADR-028). This note
covers its network bind posture. See `services/Dmail/README.md` for the full
configuration reference.

## Bind policy: loopback by default

Dmail resolves its HTTP bind address at startup as follows:

- If `DMAIL_BIND_ADDRESS` is set, it is used verbatim (after validation).
- Otherwise it defaults to `http://127.0.0.1:{DMAIL_PORT}` — loopback only.

A wildcard/all-interfaces bind (`0.0.0.0`, `::`, `*`, `+`) or any other
non-loopback address is rejected at startup **unless** `DMAIL_ALLOW_NONLOOPBACK=true`
is set. Rejection throws a fatal, actionable error naming the offending address
and the fix, before the server starts listening.

Running `dotnet run --project services/Dmail` directly is therefore safe by
default: it binds `127.0.0.1` and is reachable only from the local machine.

## Docker

The container's network namespace is the security boundary, not the in-process
bind address: `services/Dmail/Dockerfile` sets `DMAIL_ALLOW_NONLOOPBACK=true`
and `DMAIL_BIND_ADDRESS=http://+:8080` so the process binds all interfaces
*inside* the container. `services/Dmail/docker-compose.yml` then publishes that
port to host loopback only (`127.0.0.1:${DMAIL_PORT}:8080`), not all host
interfaces — so the effective exposure on the host is still loopback-only. To
reach Dmail from another machine, front it with `tailscale serve` (as with the
core network host) rather than publishing to a non-loopback host address.

Full deploy-doc coverage (auth model, key persistence, OAuth notes) is tracked
separately.
