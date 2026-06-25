# ADR-017: iOS Client Embeds Tailscale via TailscaleKit (Self-Contained Tailnet Node)

**Date:** 2026-06-09
**Status:** Proposed
**Amends:** ADR-012 (Decision 12, final bullet)

> **Amendment (2026-06-25, change `gateway-packaging`) — terminology only:** the `Dmon.Gateway` host is renamed `Dmon.Network` (tool command `ndmon`); read "gateway"/"the gateway" as "the network host" throughout. No numbered decision changes — see ADR-033.

## Context

ADR-012 made Tailscale the authentication and encryption boundary for the remote gateway: the `Dmon.Gateway` host binds loopback, `tailscale serve` fronts it on the tailnet with a Let's Encrypt cert for the server's MagicDNS name, and the iOS client connects to `wss://<host>.<tailnet>.ts.net`. That ADR's Decision 12 deliberately left the **client's** path onto the tailnet open: *"V1 expects the system Tailscale app … Embedding `libtailscale` / TailscaleKit for a self-contained app (no dependency on the Tailscale app) is a future option and changes nothing server-side."*

This ADR exercises that option. The "expects the system Tailscale app" posture has real friction for a personal product:

- The user must install **two** apps (dmon + Tailscale) and keep the Tailscale app logged in and its VPN profile installed.
- The Tailscale app installs a **system VPN configuration** that captures the device's traffic policy — a heavy, all-or-nothing footprint for what dmon needs (reach exactly one host on one port).
- Onboarding is "go install this other thing first," which is poor for a single-purpose client.

**TailscaleKit** (in `tailscale/libtailscale`, `swift/`) wraps `libtailscale.a` — the C-ABI binding over Go `tsnet` — in a `TailscaleKit.framework`. `tsnet` runs a **fully self-contained Tailscale node inside the process** using a **userspace TCP/IP stack (gVisor)**: no TUN device, no kernel networking, no `tailscaled`, and — critically on iOS — **no Network Extension and no system VPN configuration profile**. The framework exposes a Swift 6, async API (`Dial`/`Listen` shaped like `NWConnection`, plus a `URLSession` extension that routes requests to tailnet nodes through a loopback proxy, plus a `LocalAPI` for state). Separate device-only (`ios`) and simulator (`ios-sim`) frameworks are built; the `ios` framework is "free of any simulator segments and suitable for app-store submissions."

The one iOS-specific defect — issue **#15410**, titled "libtailscale doesn't work on iDevices due to apple sandboxing" — was narrower than its title: under the iOS sandbox `os.Executable()` returns an error, which `tsnet` relied on. PR **#15379** ("tsnet: Default executable name on iOS") adds an iOS branch defaulting the name to `"tsnet"`; it was **approved by a Tailscale co-founder (danderson) on 2025-03-25** and closes #15410. The userspace mechanism is otherwise sandbox-compatible by construction — it makes ordinary outbound UDP/TCP socket connections, not privileged interface operations.

This decision is **client-side only**. Nothing about the gateway, the transport (ADR-012), the wire protocol (ADR-003), or provider auth (ADR-005) changes.

## Decision

1. **The iOS dmon client embeds TailscaleKit and is itself a tailnet node.** It does not depend on the system Tailscale app being installed, logged in, or holding a VPN profile. The embedded node uses `tsnet`'s userspace gVisor stack; no Network Extension entitlement and no system VPN configuration are required. This realises the "self-contained app" future option named in ADR-012 Decision 12.

2. **The client reaches the gateway over its embedded node, unchanged above the socket.** The app dials `wss://<host>.<tailnet>.ts.net` via the TailscaleKit `URLSession` extension (a `URLSessionWebSocketTask` on a `URLSessionConfiguration.tailscaleSession(node)`). Everything ADR-012 specifies above the socket — the attach/ack/resume/heartbeat control sub-protocol, `seq` replay, generation fencing, the optional shared bearer key on the upgrade — is identical whether the tailnet path comes from the embedded node or the system app. The gateway cannot tell the difference and needs no change.

3. **Node authorization uses interactive web login, not an embedded auth key.** TailscaleKit authorizes a node either via a configured `authKey` or by watching the IPN bus for a `browseToURL` field and opening an interactive web-auth flow. dmon uses the **interactive flow**: on first run the embedded node surfaces its login URL, the app opens it (the user authenticates to *their own* tailnet), and the device is added. A long-lived auth key is **not** baked into the shipped binary — that would be a tailnet credential distributed in the app bundle. This is a one-time setup for a single-tenant, single-user product.

4. **Node identity and state live in the app's sandbox container.** `tsnet` persists node state (keys, prefs) in a directory the embedder controls; the client uses an app-container path. Node-key lifetime (persistent vs. ephemeral) and any ACL tag applied to the dmon device are deployment configuration, not code constants. Because ADR-012 is permanently single-tenant, a dmon node added to the user's tailnet inherits the same ownership-collapses-to-network-reachability model — no per-device token matrix is introduced.

5. **The server side is unchanged and continues to run real Tailscale.** The gateway runs on a home server where installing `tailscaled` (or running Go `tsnet`) is trivial and is already assumed by ADR-012. TailscaleKit's value is escaping the *can't-install-a-daemon* constraint, which exists only on the iOS client. The gateway is **not** re-platformed onto `libtailscale` via P/Invoke; it keeps binding loopback behind `tailscale serve`. (P/Invoking `libtailscale` from .NET is technically possible but buys nothing on a host that can run the daemon, and is explicitly rejected — see Alternatives.)

6. **The system Tailscale app remains a supported fallback, not a requirement.** If a user already runs the system app, the client still works (it would simply have two tailnet paths available); the embedded node is the default and the only one dmon ships against. ADR-012's "V1 expects the system Tailscale app" is downgraded to "supported but not required" for the iOS client.

## Consequences

- **Single-app onboarding.** The user installs only dmon; there is no second app, no VPN profile to approve, and no all-traffic capture. This is the central UX win and the reason for the ADR.
- **App Store viability is plausible but unproven by us.** Userspace gVisor networking is ordinary socket use, and Tailscale ships a device-only `ios` framework "suitable for app-store submissions," but dmon has not yet submitted. Treat review acceptance as a risk to retire with an actual submission, not an assumption.
- **Binary weight.** `libtailscale` embeds the Go runtime plus the gVisor netstack — tens of MB added to the IPA. Acceptable for a personal app; noted.
- **Every install is a tailnet node.** Each dmon instance appears in the tailnet admin console and counts against the tailnet's device limits — fine for single-tenant/personal use; it informs the node-key and ACL-tag story (Decision 4).
- **A new third-party native dependency with a young support story.** TailscaleKit lives in the official `tailscale/libtailscale` repo, but first-class Swift support is still being requested (issue #13937), so budget for API churn and "official-ish" support. This dependency is confined to the iOS client target; it does not reach the core, the contract packages, or the gateway.
- **No change to the contract or the core.** ADR-003/004/005/006 are untouched; the gateway code and the resume protocol are untouched. The blast radius is one iOS target.
- **Backgrounding caveat is unchanged and possibly sharper.** ADR-012 already notes iOS tears down sockets on background and that pushing a finished-turn result needs APNs. The embedded node lives in the same app process, so it dies when the app is suspended just as the socket does; resume-on-foreground via `lastSeq` (ADR-012 Decision 4) is unchanged. The embedded node does **not** give background networking that the system app's persistent tunnel would — a point in the system app's favour, retained as the fallback (Decision 6).

## Alternatives

- **Require the system Tailscale app (ADR-012's stated V1 expectation).** Zero new client dependency and a persistent tunnel that survives backgrounding. Rejected as the *default* because it forces a two-app install with a system-wide VPN profile for a single-purpose client; retained as a supported fallback (Decision 6).
- **Embed `libtailscale` in the .NET gateway via P/Invoke.** Would let the server avoid installing `tailscaled`. Rejected: the gateway runs on a home server that can trivially run the daemon (ADR-012), so this adds a fragile native-interop surface and a Go-runtime dependency to the server for no benefit. The MagicDNS `ts.net` name + `tailscale serve` already gives the gateway its tailnet identity.
- **Bake a long-lived auth key into the client.** Simplest onboarding (no interactive login). Rejected: it ships a reusable tailnet credential in the app bundle — a node-spoofing/enrollment risk — for no real gain on a one-time personal setup. Interactive web auth (Decision 3) is preferred; an ephemeral/pre-authorized key remains available for controlled provisioning if ever needed.
- **A non-Tailscale transport for the client (e.g. a public TLS endpoint + bearer auth).** Rejected: it abandons ADR-012's zero-public-exposure property and reintroduces the open-internet auth model ADR-012 deliberately did not build.

## Open Questions

- **A. WebSocket-over-`tailscaleSession` viability.** TailscaleKit documents the `URLSession` extension for HTTP requests via the loopback proxy; that a `URLSessionWebSocketTask` on that configuration carries the ADR-012 control sub-protocol end-to-end should be confirmed by a spike before committing the client to it.
- **B. Interactive auth UX on a real device.** The `browseToURL` → Safari → tailnet-login → device-added flow needs an end-to-end test on a physical device against the user's tailnet, including state persistence across app restarts.
- **C. Node-key lifetime and ACL tagging.** Persistent vs. ephemeral node keys and whether dmon devices get a dedicated ACL tag (least-privilege to the gateway port) is a deployment policy to settle when the client is built.
- **D. Background delivery.** Unchanged from ADR-012: APNs + resume-on-foreground remains the mechanism for "your turn finished" while suspended; the embedded node does not alter this and removes the system app's always-on tunnel as a mitigation when dmon is the only app.

## Relationship to other ADRs

- **ADR-012** — *Amends Decision 12's final bullet.* That bullet named embedding TailscaleKit as "a future option [that] changes nothing server-side"; this ADR adopts it as the default for the iOS client and confirms the server-side invariance. Everything else in ADR-012 (transport, session decoupling, control sub-protocol, fencing, single-tenant auth model) stands unchanged.
- **ADR-003** — the host↔core wire contract is untouched; this is below even the gateway, at the client's network layer.
- **ADR-005** — provider auth (gateway→provider) is unaffected; this concerns only the client→tailnet path.
- **ADR-006** — the permission model over the remote edge is unchanged; prompts still flow over the same WebSocket.
- **ADR-013** — orthogonal; profile selection at session creation is unaffected by how the client reaches the gateway.
