# Deploying the dmon Gateway

This document describes how to run the `Dmon.Gateway` host on a home server so that a personal iOS client can reach it securely over a Tailscale private overlay.

See [ADR-012](adrs/ADR-012-remote-session-transport.md) for the full architectural rationale and [ADR-013](adrs/ADR-013-agent-profiles.md) for agent-profile selection.

---

## Trust model

The gateway is **single-tenant**: one user, one home server, private access only.
[Tailscale](https://tailscale.com) is the **auth and encryption boundary**. The gateway
process binds exclusively to **loopback** (`http://127.0.0.1:5500` by default) — it is never
reachable from a public NIC. `tailscale serve` puts a Tailscale-managed HTTPS reverse proxy
in front of that loopback port and exposes it only inside your tailnet.

Wildcard and non-loopback binds are rejected at startup unless `Gateway:AllowNonLoopbackBind`
is explicitly set. Do not set this unless you are routing through a specific Tailscale-assigned
IP (`100.x.y.z`) and understand the security implications — the Tailscale `serve` path below
does not require it.

The optional **shared key** adds a second factor (defense-in-depth) on top of Tailscale's own
node authentication. It is not a replacement for Tailscale — it is an extra check that raises
the bar for a compromised node inside the tailnet.

---

## Step 1 — Run the gateway on the home server

The gateway reads its configuration from `appsettings.json` or environment variables.
`Dmon.Gateway` is an ASP.NET Core process; run it as a systemd service, launchd plist, or
equivalent:

```bash
# Example: run directly
GATEWAY__SharedKey="$(openssl rand -hex 32)" \
  dotnet run --project src/Dmon.Gateway --configuration Release
```

Or set values in `appsettings.json`:

```json
{
  "Gateway": {
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

## Step 2 — Expose the gateway via `tailscale serve`

`tailscale serve` provisions a **Let's Encrypt TLS certificate** for your device's MagicDNS
name (e.g. `home-server.tail1234.ts.net`) and reverse-proxies HTTPS requests to the loopback
listener. WebSocket connections are passed through transparently.

```bash
# Proxy tailnet HTTPS (443) → loopback gateway (5500)
tailscale serve https / http://127.0.0.1:5500

# Confirm the configuration
tailscale serve status
```

Your gateway WebSocket endpoint is now reachable inside the tailnet at:

```
wss://home-server.tail1234.ts.net/ws
```

Replace `home-server.tail1234.ts.net` with your device's actual MagicDNS hostname
(`tailscale status --json | jq -r '.Self.DNSName'`).

The TLS certificate is managed automatically by Tailscale using Let's Encrypt. No additional
certificate provisioning is required.

### Notes on `tailscale serve`

- Traffic is HTTPS-terminated by Tailscale before it reaches the gateway — the gateway itself
  runs plain HTTP on loopback.
- If you need the gateway to listen on a specific port other than the default (5500), update
  `Gateway:BindAddress` in config and the `--serve` target accordingly.
- `tailscale serve` exposes the port to **all** nodes in your tailnet. Restrict further using
  the ACL in Step 3.

---

## Step 3 — Tailnet ACL: restrict access to the iOS device

Add a grant in your Tailscale ACL (`admin.tailscale.com → Access controls`) that permits
only your iOS device to reach the gateway host on port 443 (the HTTPS port `tailscale serve`
listens on):

```hujson
{
  "tagOwners": {
    "tag:gateway-server": ["autogroup:owner"],
    "tag:ios-client":     ["autogroup:owner"]
  },

  "acls": [
    // iOS client may reach the gateway host on the serve port only.
    {
      "action": "accept",
      "src":    ["tag:ios-client"],
      "dst":    ["tag:gateway-server:443"]
    }
  ]
}
```

Tag the gateway host machine and the iOS device accordingly in the Tailscale admin console
(or in your `tagOwners` / `hosts` sections).

With this ACL in place, other tailnet nodes — laptops, other servers — cannot reach the
gateway port even if they are in the same tailnet.

---

## Step 4 — Optional: shared-key defense-in-depth

Set `Gateway:SharedKey` to a long random string (32+ hex characters):

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
the client app's settings and in the server's `Gateway:SharedKey`.

---

## Verifying connectivity

From a device inside the tailnet:

```bash
# Health check: expect HTTP 400 (not a WebSocket request) — confirms the endpoint is up.
curl -i https://home-server.tail1234.ts.net/ws

# With shared key — expect HTTP 401 when the key is wrong.
curl -i https://home-server.tail1234.ts.net/ws -H "Authorization: Bearer wrong"
```

A `400 Bad Request` from the health check means the gateway is reachable and the
`Authorization` check passed (or is disabled). A `401` means the key check fired. Either
confirms the proxy is in place.

---

## Current limitations

**Session creation over the gateway is not yet available.** The gateway can resume an existing
session (identified by `sessionId`) over WebSocket, but creating a brand-new session from the
iOS client is deferred to a follow-up change. Until that lands, sessions must be pre-created
by running a `dmon` terminal session on the server and noting the `sessionId`.

This limitation will be addressed by a separate OpenSpec change that wires the `createSession`
control message through the gateway and into `Dmon.Core`.

---

## Reference

| Key | Default | Description |
|-----|---------|-------------|
| `Gateway:BindAddress` | `http://127.0.0.1:5500` | Loopback address the gateway binds. |
| `Gateway:SharedKey` | *(none)* | Pre-shared bearer key for defense-in-depth. Empty or absent disables the check. |
| `Gateway:AllowNonLoopbackBind` | `false` | Allow binding a non-loopback address (e.g. a Tailscale IP). |
| `Gateway:IdleDetachedTtlMinutes` | `15` | How long an idle detached session survives before reaping. |
| `Gateway:RunningTurnTtlMinutes` | `60` | Absolute ceiling for a detached in-flight session. |
| `Gateway:MaxConcurrentHandlers` | `10` | Maximum number of concurrent live session handlers. |
| `Gateway:HeartbeatIntervalSeconds` | `25` | Ping interval; missed pong → detach. |
