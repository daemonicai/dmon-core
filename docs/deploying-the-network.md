# Deploying the dmon Network host

This document describes how to run the `Dmon.Network` host on a home server so that a personal iOS client can reach it securely over a Tailscale private overlay.

See [ADR-012](adrs/ADR-012-remote-session-transport.md) for the full architectural rationale and [ADR-022](adrs/ADR-022-composition-root-registration-facets.md) for agent-selection (each agent is its own `.cs` composition root).

---

## Trust model

The network host is **single-tenant**: one user, one home server, private access only.
[Tailscale](https://tailscale.com) is the **auth and encryption boundary**. The network host
process binds exclusively to **loopback** (`http://127.0.0.1:5500` by default) — it is never
reachable from a public NIC. `tailscale serve` puts a Tailscale-managed HTTPS reverse proxy
in front of that loopback port and exposes it only inside your tailnet.

Wildcard and non-loopback binds are rejected at startup unless `Network:AllowNonLoopbackBind`
is explicitly set. Do not set this unless you are routing through a specific Tailscale-assigned
IP (`100.x.y.z`) and understand the security implications — the Tailscale `serve` path below
does not require it.

The optional **shared key** adds a second factor (defense-in-depth) on top of Tailscale's own
node authentication. It is not a replacement for Tailscale — it is an extra check that raises
the bar for a compromised node inside the tailnet.

---

## Step 1 — Run the network host on the home server

The network host reads its configuration from `appsettings.json` or environment variables.
`Dmon.Network` is an ASP.NET Core process; run it as a systemd service, launchd plist, or
equivalent.

Install the network host as a dotnet tool via `make network` (from the repo root), or:

```bash
dotnet tool install --global Dmon.Network
```

This places the `ndmon` command at `~/.dotnet/tools/ndmon`. Set `DMON_NETWORK_PATH` to
override the binary path (useful when running a local build instead of the installed tool).

Run directly (dev/testing):

```bash
# Example: run directly
NETWORK__SharedKey="$(openssl rand -hex 32)" \
  dotnet run --project frontends/Dmon.Network --configuration Release
```

Or set values in `appsettings.json`:

```json
{
  "Network": {
    "BindAddress": "http://127.0.0.1:5500",
    "SharedKey": "<your-secret>",
    "IdleDetachedTtlMinutes": 15,
    "RunningTurnTtlMinutes": 60,
    "MaxConcurrentHandlers": 10
  }
}
```

`BindAddress` defaults to `http://127.0.0.1:5500`. Leave it as-is; `tailscale serve` will
forward HTTPS traffic to it.

---

## Step 2 — Expose the network host via `tailscale serve`

`tailscale serve` provisions a **Let's Encrypt TLS certificate** for your device's MagicDNS
name (e.g. `home-server.tail1234.ts.net`) and reverse-proxies HTTPS requests to the loopback
listener. WebSocket connections are passed through transparently.

```bash
# Proxy tailnet HTTPS (443) → loopback network host (5500)
tailscale serve https / http://127.0.0.1:5500

# Confirm the configuration
tailscale serve status
```

Your network host WebSocket endpoint is now reachable inside the tailnet at:

```
wss://home-server.tail1234.ts.net/ws
```

Replace `home-server.tail1234.ts.net` with your device's actual MagicDNS hostname
(`tailscale status --json | jq -r '.Self.DNSName'`).

The TLS certificate is managed automatically by Tailscale using Let's Encrypt. No additional
certificate provisioning is required.

### Notes on `tailscale serve`

- Traffic is HTTPS-terminated by Tailscale before it reaches the network host — the network host itself
  runs plain HTTP on loopback.
- If you need the network host to listen on a specific port other than the default (5500), update
  `Network:BindAddress` in config and the `--serve` target accordingly.
- `tailscale serve` exposes the port to **all** nodes in your tailnet. Restrict further using
  the ACL in Step 3.

---

## Step 3 — Tailnet ACL: restrict access to the iOS device

Add a grant in your Tailscale ACL (`admin.tailscale.com → Access controls`) that permits
only your iOS device to reach the network host on port 443 (the HTTPS port `tailscale serve`
listens on):

```hujson
{
  "tagOwners": {
    "tag:network-host": ["autogroup:owner"],
    "tag:ios-client":   ["autogroup:owner"]
  },

  "acls": [
    // iOS client may reach the network host on the serve port only.
    {
      "action": "accept",
      "src":    ["tag:ios-client"],
      "dst":    ["tag:network-host:443"]
    }
  ]
}
```

Tag the network host machine and the iOS device accordingly in the Tailscale admin console
(or in your `tagOwners` / `hosts` sections).

With this ACL in place, other tailnet nodes — laptops, other servers — cannot reach the
network host port even if they are in the same tailnet.

---

## Step 4 — Optional: shared-key defense-in-depth

Set `Network:SharedKey` to a long random string (32+ hex characters):

```bash
openssl rand -hex 32
# e.g. a3f7b2...
```

Every WebSocket upgrade must then carry:

```
Authorization: Bearer <your-secret>
```

A missing or wrong key is rejected with **HTTP 401 before a socket is opened**. The response
carries no `WWW-Authenticate` header by design — this avoids advertising the authentication
scheme to a scanner that probes the endpoint.

The iOS client must send this header on the WebSocket upgrade request. Configure the key in
the client app's settings and in the server's `Network:SharedKey`.

---

## Verifying connectivity

From a device inside the tailnet:

```bash
# Health check: expect HTTP 400 (not a WebSocket request) — confirms the endpoint is up.
curl -i https://home-server.tail1234.ts.net/ws

# With shared key — expect HTTP 401 when the key is wrong.
curl -i https://home-server.tail1234.ts.net/ws -H "Authorization: Bearer wrong"
```

A `400 Bad Request` from the health check means the network host is reachable and the
`Authorization` check passed (or is disabled). A `401` means the key check fired. Either
confirms the proxy is in place.

---

## Current limitations

**Session creation over the network host is not yet available.** The network host can resume an existing
session (identified by `sessionId`) over WebSocket, but creating a brand-new session from the
iOS client is deferred to a follow-up change. Until that lands, sessions must be pre-created
by running a `dmon` terminal session on the server and noting the `sessionId`.

This limitation will be addressed by a separate OpenSpec change that wires the `createSession`
control message through the network host and into `Dmon.Core`.

---

## Reference

| Key | Default | Description |
|-----|---------|-------------|
| `Network:BindAddress` | `http://127.0.0.1:5500` | Loopback address the network host binds. |
| `Network:SharedKey` | *(none)* | Pre-shared bearer key for defense-in-depth. Empty or absent disables the check. |
| `Network:AllowNonLoopbackBind` | `false` | Allow binding a non-loopback address (e.g. a Tailscale IP). |
| `Network:IdleDetachedTtlMinutes` | `15` | How long an idle detached session survives before reaping. |
| `Network:RunningTurnTtlMinutes` | `60` | Absolute ceiling for a detached in-flight session. |
| `Network:MaxConcurrentHandlers` | `10` | Maximum number of concurrent live session handlers. |
| `Network:HeartbeatIntervalSeconds` | `25` | Ping interval; missed pong → detach. |
