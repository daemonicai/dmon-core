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

The optional **per-device key set** adds a second factor (defense-in-depth) on top of Tailscale's
own node authentication. It is not a replacement for Tailscale — it is an extra check that raises
the bar for a compromised node inside the tailnet. The host validates each WebSocket upgrade against
a `devices.json` key store; if the store is absent or empty the check is disabled and access rests
on Tailscale alone.

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
# Example: run directly (no auth env needed; auth is enabled by populating the key store)
dotnet run --project frontends/Dmon.Network --configuration Release
```

Or set values in `appsettings.json`:

```json
{
  "Network": {
    "BindAddress": "http://127.0.0.1:5500",
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

## Step 4 — Optional: per-device key enrolment

The network host supports a **per-device key set** as a second authentication factor on top of
Tailscale. This is configured via a `devices.json` file. When the file is absent or the
`devices` array is empty, the key check is disabled and your tailnet ACL is the sole gate —
you cannot lock yourself out by leaving the store absent.

### Key store location

By default the store directory is `~/.dmon/network/`. The host expects:

- `~/.dmon/network/devices.json` — operator-managed; contains the device registry.
- `~/.dmon/network/lastseen.json` — host-managed; records per-device last-seen timestamps.

Override the directory with `Network:DeviceKeyStoreDirectory` in config or the
`NETWORK__DeviceKeyStoreDirectory` environment variable.

### `devices.json` schema

```json
{
  "schemaVersion": 1,
  "devices": [
    {
      "keyId": "iphone-home",
      "name": "Personal iPhone",
      "secretHash": "a3f7b2c1d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1",
      "createdAt": "2026-06-29T00:00:00Z",
      "revokedAt": null
    }
  ]
}
```

Field details:

| Field | Type | Notes |
|-------|------|-------|
| `schemaVersion` | integer | Must be `1`; any other value is rejected at startup (fatal) or ignored at hot-reload (fail-closed to last-good). |
| `keyId` | string | Stable identifier for this device; used in logs and `lastseen.json`. |
| `name` | string | Human-readable label (no functional role). |
| `secretHash` | string | SHA-256 **hex** digest of the device's raw token. Case-sensitive; entries with a blank value are excluded from the active set. |
| `createdAt` | ISO-8601 | Record creation timestamp. |
| `revokedAt` | ISO-8601 or `null` | Set to revoke. Revoked entries are excluded from the active set; any live connection for that `keyId` is fenced immediately on hot-reload. |

Field names are **case-sensitive** — the reader does not perform case-insensitive matching.

### Generating a device token and `secretHash`

On the device, generate a high-entropy token and derive its SHA-256 hex digest. The raw token
**never** leaves the device and is **never** stored on the host; only the hash goes in
`devices.json`.

```bash
# Generate a random 32-byte token (64 hex chars)
TOKEN=$(openssl rand -hex 32)
echo "Token (keep on device only): $TOKEN"

# Derive the secretHash for devices.json
echo -n "$TOKEN" | openssl dgst -sha256
# or: echo -n "$TOKEN" | shasum -a 256
```

Copy the hex digest into the `secretHash` field. Configure the raw token in the iOS client's
settings (it presents it as `Authorization: Bearer <token>` on each WebSocket upgrade).

### How authentication works

On every WebSocket upgrade the host:

1. Reads the `Authorization` header (case-insensitive scheme match: `Bearer`).
2. SHA-256-hashes the presented token.
3. Constant-time-compares the hash against every active entry in `devices.json` (no early-out
   to prevent timing side-channels).
4. A match authorises the connection and tags it with the matched `keyId`.
5. No match → **HTTP 401**, connection refused. No `WWW-Authenticate` header is sent (avoids
   advertising the auth scheme to scanners).

### Enforcement threshold

- **Empty or absent `devices.json`** → auth disabled; access rests on Tailscale alone.
- **First active entry present** → enforcement is on; every upgrade must present a valid token.

### Revocation

To revoke a device, set its `revokedAt` to any ISO-8601 timestamp. On the next hot-reload
(within ~250 ms of saving the file) that `keyId` leaves the active set **and** any live WebSocket
connection carrying it is immediately fenced/aborted — no host restart required.

### Hot-reload behaviour

The host watches `devices.json` via a 250 ms debounced file-system watcher. Changes take effect
without restarting `ndmon`. If the file is malformed or unreadable at runtime the host retains
the **last-good** key set and logs a warning (fail-closed — it does not fail open). A malformed
`devices.json` at **startup** is **fatal** (the host exits with code 1).

### dmonium pairing (not yet shipped)

Per [ADR-018](adrs/ADR-018-per-device-gateway-keys.md), dmonium is the intended operator pairing
surface: it will mint a device entry, write `devices.json`, and display a QR code for the iOS
client to scan. **This is not yet implemented.** The `NetworkManager` in dmonium currently
supervises the host process only. Until pairing ships, hand-editing `devices.json` is the
primary enrolment path.

---

## Verifying connectivity

From a device inside the tailnet:

```bash
# Health check: expect HTTP 400 (not a WebSocket request) — confirms the endpoint is up.
curl -i https://home-server.tail1234.ts.net/ws

# With a device token absent from devices.json — expect HTTP 401.
curl -i https://home-server.tail1234.ts.net/ws -H "Authorization: Bearer wrong-token"

# With a valid token — expect HTTP 101 Switching Protocols (WebSocket upgrade).
curl -i https://home-server.tail1234.ts.net/ws -H "Authorization: Bearer $TOKEN"
```

A `400 Bad Request` from the health check (no auth header) means the network host is reachable
and the device-key check passed (or is disabled — no active entries). A `401` means the token
was rejected. Either curl output confirms the Tailscale proxy is in place.

After a successful attach the host writes the device's last-seen timestamp to
`~/.dmon/network/lastseen.json`, throttled by `Network:LastSeenThrottleSeconds` (default 60 s).

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
| `Network:AllowNonLoopbackBind` | `false` | Allow binding a non-loopback address (e.g. a Tailscale IP). |
| `Network:IdleDetachedTtlMinutes` | `15` | How long an idle detached session survives before reaping. |
| `Network:RunningTurnTtlMinutes` | `60` | Absolute ceiling for a detached in-flight session. |
| `Network:MaxConcurrentHandlers` | `10` | Maximum number of concurrent live session handlers. |
| `Network:HeartbeatIntervalSeconds` | `25` | Ping interval; missed pong → detach. |
| `Network:CreateHandshakeTimeoutSeconds` | `30` | Timeout for the create→load handshake after spawning a core. |
| `Network:DeviceKeyStoreDirectory` | `~/.dmon/network/` | Directory containing `devices.json` and `lastseen.json`. Empty = use default. |
| `Network:LastSeenThrottleSeconds` | `60` | Minimum seconds between `lastseen.json` writes for the same device. |
| `Network:WorkspaceRoot` | *(cwd)* | Root directory the spawned core inherits as its working directory. |
