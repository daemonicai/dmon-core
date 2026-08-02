# Design — dmon-home Foundations and Text Loop

## Context

`home/PRD.md` (2026-07-31) specifies a native macOS application that supervises the Mac-side dmon
stack, hosts a realtime voice loop over a Bluetooth headset, and is the machine iOS clients reach over
Tailscale. This change implements its **Phase 0 (Foundations)** and **Phase 1 (Text loop)** only.

The relevant existing state:

- **`frontends/Dmon.Network`** already implements the ADR-012 gateway: Kestrel on
  `http://127.0.0.1:5500`, WebSocket at `/ws`, device-key auth, per-session core spawning,
  attach/replay/resume, heartbeats, TTLs and a concurrency cap.
- **`core/Dmon.Runtime`** already implements core process launching, resolution, protocol version
  negotiation and RPC correlation — in C#.
- **`daemon/Daemon.App`** ("dmonium") is an existing ~2k-line Swift/SwiftUI macOS app that supervises
  `Dcal`, `Dmail` and `Dmon.Network`, runs Tailscale/calendar/mail/egress monitors, and presents a
  window-primary dashboard. It is built by `swift build` with no `.app` bundle and no Xcode project.
- The monorepo's top-level bucket set is fixed by **ADR-025 D2** and **ADR-028**, and mirrored as a
  standing requirement in `openspec/specs/monorepo-layout/spec.md`. `home/` is not in it.
- **ADR-036** makes an empty or absent device-key store *auth-disabled on a loopback bind* and
  *fail-closed on a non-loopback bind*.

The Product Owner settled the product-level questions before this change was opened; the decisions
below record them and the technical choices that follow from them.

## Goals / Non-Goals

**Goals:**

- A runnable, installable `.app` that a human can launch and immediately see working.
- Supervision that survives development: adopt already-running children rather than respawning them,
  and never leak a child that holds unified memory.
- Prove the ADR-012 protocol end-to-end — create, attach, submit a turn, render streamed output —
  with no audio in the path, so protocol bugs and audio bugs can never be confused for one another.
- Establish package seams now that the audio phases drop into without restructuring.
- Verify the microphone TCC prompt appears **before** any audio code exists.

**Non-Goals:**

- Any audio: capture, VAD, STT, TTS, playback, barge-in, headset button (PRD Phases 2–4).
- Device-directedness classification and memory gating (PRD Phase 5).
- Starting the mlx and speech children. Their supervision *shape* is designed here; their actual
  launch arrives with the phase that needs them.
- Deleting `daemon/Daemon.App`, or touching its CI job or release artifact.
- A release artifact for the new app. It gains one when it does something worth shipping.
- Remote wake of a sleeping Mac — pinned out of scope by PRD §7.3.

## Decisions

### D1 — `home/` is a new top-level monorepo bucket

The macOS host lives at `home/`, not `daemon/` or `frontends/`.

*Rationale.* `frontends/` is reserved for processes that **are** dmon-protocol surfaces (Terminal,
Network, Desktop) — the host is a client of one, but its centre of gravity is the machine, not the
protocol. `daemon/` holds the Daemon product's *composition* (ADR-028 D1). The Mac host is neither: it
is the home/ambient surface that hosts the stack. Given the PO has also decided dmonium is superseded
rather than extended, putting the successor into `daemon/` alongside the thing it replaces would make
the eventual retirement harder to read, not easier.

*Cost.* ADR-025 D2 and ADR-028 enumerate the bucket set exhaustively, and `monorepo-layout/spec.md`
turns that into a testable requirement. Both need amending — hence ADR-037 and a MODIFIED spec delta.

*Alternatives.* `daemon/Daemon.MacHost` — cheaper (no ADR, no spec delta) but muddles the retirement
and mis-files the product. `frontends/` — the same cost as `home/` (still a bucket-semantics change,
since `frontends/` is currently all .NET) with a worse fit.

### D2 — The host is a gateway client, not an ADR-003 stdio host

The host connects to `Dmon.Network` over a WebSocket to a **configured gateway endpoint** and speaks
the same `gw` control frames as the iOS client. It does not speak ADR-003 stdio to `dmon-core`.

**The endpoint is configuration, not an assumption** (PRD §7.4: *"The gateway is reached by hostname,
not assumed to be local"*). In the first deployment it resolves to loopback, because the host and the
back-end are the same machine. It is not required to: a plausible future topology puts `dmon-home` on
a Mac mini in a living space and the models on a separate over-provisioned machine elsewhere on the
LAN. Nothing in this change builds for that split, but nothing in this change may foreclose it — the
endpoint stays a config value throughout. See "Known future topology" below for what such a split
would cost.

*Rationale* (PRD §2.1, which this change adopts unchanged):

- Speaking stdio directly means reimplementing `Dmon.Runtime` in Swift — launcher, resolver, version
  negotiation, transport, RPC correlation, session lifecycle.
- **ADR-003 commands carry no session identifier.** Session is ambient per-core-process state mutated
  by `session.create` / `session.load`, so one stdio channel cannot serve the local voice loop and a
  remote iOS client concurrently. The gateway solves this by spawning one core per session; bypassing
  it means building a second, competing core-spawning path.
- `attach` / `attached` already carry `lastSeq`, `headSeq` and `generation`, so replay and resume after
  a dropped connection come free — worth having locally too.
- The local and iOS clients become the same client, over the same protocol, tested once.

*Cost.* One WebSocket hop — loopback in the co-located deployment.

*Reversibility.* The transport sits behind a Swift protocol (`GatewayTransport`); nothing above it may
reference `URLSessionWebSocketTask` or any other transport-specific type. Its day-one payoff is
**testability** — an in-memory conformer drives the handshake, turn submission and rendering with no
network. Its second payoff is topology: a remote or TLS transport is a conforming type.

Note that PRD §2.1's stated fallback — "a Swift port of `Dmon.Runtime` and a direct stdio core" — is
**contingent on co-location**, and the known future topology below would foreclose it: you cannot
spawn a core over stdio on a machine you are not running on. This does not weaken D2; it strengthens
it. Being a gateway client is what makes the split *possible*, and the stdio escape hatch should not
be relied on as a general-purpose retreat.

### D3 — Only four SPM packages now; the audio packages arrive with their phase

PRD §2.3 lists seven packages. This change creates **`Supervisor`**, **`GatewayClient`**, **`Power`**
and the **`DmonHomeApp`** app target. `AudioEngine`, `Speech` and `Directedness` are **not** created.

*Rationale.* Empty placeholder packages are dead scaffolding — they pass review, then rot. The repo
already holds this principle for .NET: `monorepo-layout` requires that "a role bucket with no current
members SHALL NOT exist as a directory". The same logic applies to a target with no code. Each audio
package is created by the change that first puts code in it.

`Power` is kept separate despite being small in this change (it holds only the `beginActivity`
assertion for now). PRD §7.2's sleep/wake observation lands there next, and Supervisor should not grow
a power-policy responsibility. It ships real code now, so it is a seam, not scaffolding.

### D4 — XcodeGen, and the app target is a shell

`project.yml` is checked in; `.xcodeproj` is generated and **never hand-edited** (PRD §11). All logic
lives in the local SPM packages so it is testable headlessly by `swift test` without an Xcode host
application, and so agents editing the project cannot mangle a large project file.

*Alternative.* Tuist — equivalent capability, heavier toolchain. XcodeGen's `project.yml` is a single
readable file, which matters more here than Tuist's extra power.

### D5 — A real `.app` bundle, App Sandbox disabled, and the TCC prompt verified by a human

The app is an unsandboxed `.app` bundle with `NSMicrophoneUsageDescription` in `Info.plist`.

*Rationale.* The sandbox blocks spawning a Python interpreter from `/opt/homebrew` and reading
multi-gigabyte model files — both load-bearing for later phases. Unsandboxed means no App Store
distribution, which is not wanted (PRD §3). Without a proper bundle *and* the usage string, the
microphone silently returns silence and no prompt ever appears — a failure that looks exactly like a
broken audio pipeline.

*This is verified by a human in this change*, before any audio code exists, precisely so that later
audio debugging never has to ask "is it the code or is it TCC?". There is no automated substitute.

### D6 — Reattach-first supervision, designed for the full child inventory

Health-check the known endpoint first; **adopt** a live process; spawn only if nothing answers
(PRD §6.1). Per child: startup ordering, health check with timeout, crash detection with exponential
backoff, dependency-ordered graceful shutdown, and **process-group kill on exit**.

*Rationale.* A crash in the app takes down anything spawned as a child with inherited pipes. Reloading
a 26B model costs tens of seconds and gigabytes of disk reads, and that will happen constantly during
audio development. Because mlx is reached over a socket it can outlive the host, so it should.
Process-group kill matters because orphaned mlx processes holding unified memory make the *next*
launch fail allocation for no visible reason.

The child model is designed for the **full inventory** — gateway, dcal, dmail, mlx reasoner, mlx
triage, speech sidecar, plus the Tailscale / calendar-sync / mail / egress monitors — because the host
must eventually be a superset of dmonium, not of PRD §2.2's three. This change **starts** only the
gateway; the rest are configuration in a model that already accommodates them. Getting the abstraction
wrong now means reworking it seven times later.

### D7 — STT/TTS run in a Python/mlx sidecar (answers PRD Q4)

*Rationale.* Two MLX runtimes competing for unified memory is worse than one socket hop. The sidecar
keeps Parakeet and the TTS models co-resident in the same MLX process as the models they share memory
pressure with, and it matches the existing `Dmon.Providers.Mlx` uv-venv pattern already in the repo.

*Alternative.* In-process Swift via sherpa-onnx (Parakeet TDT, Silero VAD and several TTS families
with Swift bindings) removes a hop and a supervised child, but introduces a second ONNX/Metal runtime
alongside mlx_lm. Note this only settles **STT/TTS**: Silero **VAD** still runs host-side via ONNX
Runtime with the CoreML execution provider (PRD §4.4), because round-tripping to a backend to decide
whether someone is speaking is not acceptable.

No code in this change; recorded so the Phase 3 packages are shaped for a socket client.

### D8 — Product name `dmon-home`, bundle id `ai.daemonic.dmon-home`

dmonium's `ai.daemonic.dmonium` retires with its code rather than being inherited. A distinct bundle
id means the two apps can be installed side by side during the parity period without fighting over
Keychain items, login-item registration or TCC grants — which is what makes D9's staged retirement
safe. The cost is that the new app re-prompts for its own TCC permissions, which is correct anyway
since it is a different binary.

### D9 — dmonium is retired at parity, by a later change

This change lands **alongside** `daemon/Daemon.App` and does not touch it, its CI job, or its release
artifact. A later change deletes it once the Mac host demonstrably covers its surface.

**Costed retirement blast radius**, recorded now so the follow-up change is not a discovery exercise:

| Target | What changes |
|---|---|
| `.github/workflows/ci.yml` | The `Daemon.App (macOS)` job (~lines 105–136) and its path filter |
| `.github/workflows/release.yml` | The dmonium `.app`/`.dmg`/zip artifact path (~lines 133–208) |
| `Makefile` | `daemon-app` build/test targets (~lines 66, 69) |
| `.github/area-map.yml` | The comment asserting `daemon/Daemon.App/**` is the sole unmapped Swift path |
| ADR-025 | D10 — release matrix artifact sources |
| ADR-028 | D1 (bucket membership), D2 (`dmonium` placement and bundle id), D6 (artifact source) |
| `openspec/specs/monorepo-layout/spec.md` | Bucket membership and the Swift-exclusion scenario |
| `openspec/specs/continuous-integration/spec.md` | The macOS Swift job requirement |
| `openspec/specs/package-publishing/spec.md` | The app-artifact family naming dmonium |

**Parity is not just the three PRD children.** dmonium's `DaemonController.bootstrap()` starts and
health-registers seven — Network(0), Dcal(1), Dmail(2), Tailscale(3), Calendar Sync(4), Mail(5),
Egress Endpoint(6) — plus `Keychain` and `LoginItemManager`. All of that must exist in the Mac host
before deletion.

### D10 — CI builds and tests the new package; no release artifact yet

A second macOS job with its own path filter scoped to `home/**`. The `continuous-integration` spec's
Swift requirement currently names `daemon/Daemon.App` as *the* Swift package and hard-codes its path
filter, so it generalises to "each Swift package, independently filtered".

No `release.yml` change. The app is not shippable at Phase 1 and an artifact for it would be shipping
a text-only shell.

### D11 — Correct the stale `ControlFrames.cs` comments (answers PRD Q3)

`AttachedFrame` says `generation` is "issued here but not enforced until Group 6"; `AckFrame` says
"dedup logic is Group 5". **Both are implemented.** Verified against the code:

- `CommandAdmission { Accepted, Duplicate }` is the result type of `SessionHandler.TryAdmitCommand`.
- `SessionHandler.Attach` captures the prior connection, increments `_generation` under lock, swaps
  `_connection`, maintains the cross-session `keyId` index atomically, and aborts the evicted
  connection outside the lock — its own comment cites "Group 6 / 6.3".
- `openspec/specs/remote-session-gateway/spec.md` carries **accepted** requirements for "Command
  idempotency across reconnects" and "Stale-connection fencing and single active writer", including
  the older-generation-fenced, new-attach-evicts-prior and revocation-fencing scenarios.

So the client may rely on dedup and fencing semantics. The requirements are already correct and
unchanged — this is a **comment-only** fix, with no spec delta on `remote-session-gateway`.

### D12 — Wire version `0.2`, `Major.Minor` compatibility

Host and core are compatible when `Major.Minor` match. The codec follows `WireSerializerOptions.Default`
semantics: camelCase, out-of-order discriminators tolerated, nulls omitted. Frames carrying a `gw`
field are gateway control frames; frames without one are ADR-003 commands or events forwarded
byte-unchanged. A mismatch is surfaced as a clear, actionable error in the UI, not a silent failure.

### D13 — Device key: loopback-tolerant now, self-provisioning available

The gateway binds loopback-only and `tailscale serve` fronts it for iOS. Per ADR-036, an empty or
absent device-key store is **auth-disabled on a loopback bind** — so a fresh developer machine
connects with no key at all, which is the least-friction path and matches the PRD's "optimise for time
from launch to a spoken exchange".

When `devices.json` *is* populated (which it will be, for the iOS client), the host needs its own key
(PRD §8). `secretHash` is hex-encoded **SHA-256 of the token**, compared with
`CryptographicOperations.FixedTimeEquals` — a plain digest rather than a password KDF, which is
correct because the token is high-entropy random rather than a user-chosen password. That is
reproducible in Swift with CryptoKit, so the host can self-provision: generate a random token, store
the secret in the Keychain, and append `{keyId, name, secretHash, createdAt}` to `devices.json`.

Self-provisioning is safe here because the app runs unsandboxed as the same user that owns the file,
on the same machine, against a loopback-only listener — **process position, not network position**.
The write grants the host no capability it did not already have. It is nonetheless a security-sensitive
write; see Q5 for the alternative that was considered and declined.

**This is a co-located-only mechanism, and deliberately so.** It works *because* the host and the
gateway share a filesystem. Under the split topology recorded above, the host cannot write
`devices.json` on the back-end machine at all, and ADR-036 additionally makes a device key
*mandatory* rather than optional on the resulting non-loopback bind. A split therefore requires the
`ndmon` provisioning verb — not as a preference, but as the only available route. Do not let this
decision read as a general position on provisioning; it is a position on provisioning **when the two
sides share a machine**.

Request provenance is stamped so `AbilityRegistry.ForScope` can later distinguish a request from the
phone from one originating on the machine holding the models (PRD §8). Cheap now, awkward to retrofit.

## Known future topology — a split back-end

The Product Owner has flagged (2026-08-02) that `dmon-home` and the back-end may in future run on
**separate physical machines on the same LAN** — the host on a Mac mini in a living space, the models
on a far larger machine tucked away. This change **does not build for that**, and sections 1–10 assume
co-location. It is recorded because the cheap non-foreclosing choices must be made now, and because
three things that look settled today are settled only for the co-located case:

| Concern | Co-located (this change) | What a split would require |
|---|---|---|
| Gateway endpoint | Loopback | Already config (D2) — **no change**, this is why it stays a config value |
| Device-key provisioning | Host self-provisions (D13) | **Impossible** — the host cannot write `devices.json` on another machine. Needs the `ndmon` provisioning verb |
| Auth requirement | Optional; empty store is auth-disabled on loopback (ADR-036) | **Mandatory** — ADR-036 fails closed on a non-loopback bind, and `AllowNonLoopbackBind` must be opted into |
| Transport security | Loopback needs none; `tailscale serve` gives iOS a valid cert on `*.ts.net` | Undecided — a bare LAN hostname has no cert, so either the link rides Tailscale too, or it is plaintext carrying a bearer token |
| Supervision | Host supervises its children | Gateway, both mlx runtimes, dcal and dmail are all remote. **Largely absorbed already**: the supervision spec distinguishes monitors — "health sources that are never spawned, adopted or killed" — and a remote process is exactly that |
| Speech sidecar (D7) | Co-resident with the models | Genuinely open — audio hardware is where the *person* is, but the memory is on the other box. Either speech runs on the smaller machine, or raw audio crosses the LAN on the most latency-sensitive path in the system |

The two rows worth carrying forward are **provisioning** and **speech location**. Neither blocks this
change; both should be reopened before a split is attempted rather than discovered during one.

## Risks / Trade-offs

- **The bucket change is the widest-blast-radius part of a change that is mostly new files.** Two
  standing specs and two ADRs move. → Confine it to ADR-037 plus two MODIFIED deltas, land it as the
  first section, and keep every other section additive.
- **Reimplementing the device-key hash in Swift couples two languages to one format.** If the C# side
  ever moves to a KDF, a Swift client silently fails to authenticate. → Keep the hash computation in
  one small, well-named Swift type with a test that pins the exact hex digest of a known token, so the
  coupling is visible and a divergence fails loudly. See Open Questions.
- **Two Swift apps in the tree during the parity period**, both able to supervise `Dmon.Network`. If
  both run, both try to own the gateway process. → Reattach-first adoption (D6) makes the second one
  adopt rather than double-spawn, which is the correct behaviour anyway; distinct bundle ids (D8) keep
  their other state separate.
- **Nothing in this change proves the app is *good*, only that it works.** The PRD is explicit that
  audio correctness is judged by ear. → That is exactly why the log pane and a runnable app are Phase
  0 deliverables rather than a later nicety.
- **macOS CI runners are costly.** A second macOS job doubles that cost on Swift changes. → Independent
  path filters mean each job runs only for its own package, and neither is triggered by .NET changes.
- **Designing the supervisor for nine children while starting one risks over-abstraction.** → Mitigated
  by the inventory being *known and enumerated*, not speculative: it is dmonium's existing seven plus
  the PRD's mlx and speech children.

## Migration Plan

No runtime migration — the new app is additive and there are no production deployments.

Ordering: `home/` bucket and ADR-037 first (everything else depends on the bucket existing), then the
app shell and bundle, then supervision, then the gateway client. The microphone TCC verification gate
sits at the end of Phase 0, before Phase 1 begins.

Rollback is deletion of `home/` plus reverting the two spec deltas and the ADR; nothing else in the
repo depends on it.

dmonium remains fully functional and shipping throughout. Its retirement is D9's separate change, and
until that lands the Product Owner always has a working supervisor.

## Open Questions

**Q1 — Metal from a background context** *(PRD §9 Q1 — noted, not resolved)*. If the supervisor is
later split into a `LaunchDaemon` so it survives logout and reboot, it runs outside the GUI session,
where Metal/GPU initialisation has historically been unreliable. **`mlx_lm` must be tested for Metal
initialisation from a daemon context before committing to that split.** A `LaunchAgent` (user session,
requires login) sidesteps it entirely. Does not bite here — the app is foreground-launched — but it
constrains the always-on story, so it should not be discovered late.

**Q2 — Triage endpoint access** *(PRD §9 Q2 — deferred to Phase 5)*. Device-directedness must be
classified *before* a turn is submitted, so the host needs a direct line to the triage model rather
than going through the gateway and core. Whether that is a direct mlx endpoint call or a lightweight
classification path exposed by the speech service is a Phase 5 decision. Recorded here because it is
the one place the "everything goes through the gateway" stance (D2) will need a deliberate exception.

**Q3 — Gateway implementation completeness** — **resolved**, see D11. Dedup and fencing are
implemented; the annotations were stale.

**Q4 — Speech service hosting** — **resolved**, see D7. Sidecar.

**Q5 — Device-key provisioning ownership** — **resolved 2026-08-02 by the Product Owner: the host
self-provisions**, per D13. The alternative — adding a provisioning verb to `ndmon` (there is
currently no device-add CLI at all; `Program.cs` has no verbs) and shelling out to it, keeping the
hash in exactly one language — is **not** taken. It would be .NET work in `frontends/`, a prerequisite
change ahead of section 6, and the coupling it avoids is cheaply contained by the pinned-digest test
in task 6.5.

Note on *why* this is safe, because the reason matters if the deployment ever moves: it is **not**
that the host sits on a trusted LAN. A LAN is not a trust boundary, and ADR-036 deliberately makes an
empty key store **fail closed** on a non-loopback bind for exactly that reason. Self-provisioning is
safe because of **process position, not network position** — `dmon-home` runs unsandboxed as the same
user that owns `~/.dmon/network/devices.json`, on the same machine, against a loopback-only listener.
The write grants it no capability it did not already have. Should the host ever be split into a
`LaunchDaemon` running as a different principal (see Q1), or should the gateway ever take a
non-loopback bind, this justification lapses and provisioning ownership must be revisited.
