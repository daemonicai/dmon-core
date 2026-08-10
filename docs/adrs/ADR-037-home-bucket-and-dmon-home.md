# ADR-037: The home/ Bucket and dmon-home — a Gateway-Client macOS Host Superseding dmonium

**Date:** 2026-08-02
**Status:** Accepted
**Amends:** ADR-025 (D2 bucket set — adds `home/`; D10 release-matrix artifact sources — prospectively, when `dmon-home` gains an artifact, which per Decision 5 will be **arm64-only**), ADR-028 (D1 bucket membership; D2 `dmonium` placement, product name and the `ai.daemonic.dmonium` bundle id; D6 artifact source — prospectively, on the same terms)
**Builds on:** ADR-012 (remote session transport / the `gw` control-frame sub-protocol), ADR-034 (mlx local runtime), ADR-036 (loopback-vs-non-loopback auth posture), ADR-003 (the wire contract this host deliberately does not speak directly), ADR-024 / ADR-035 (app-artifact family, independently versioned)

> **Amendment (2026-08-02, change `dmon-home-foundations`) — speech location; refines Decision 4's rationale, no decision changes.**
> The speech sidecar **runs on the `dmon-home` host under any topology**: STT and TTS co-locate with the application rather than with the reasoner. (Silero VAD was already host-side per Decision 4 and is unaffected.) Should the back-end ever move to a separate machine, speech follows the host, so raw audio never crosses the network on the latency-critical path.
>
> This makes **both** of Decision 4's stated rationales conditional on the co-located deployment, and the leading one does not merely weaken:
> - *"two MLX runtimes competing for unified memory is worse than one socket hop"* — under a split there is exactly **one** MLX runtime on the `dmon-home` machine, because the reasoner is elsewhere. The memory-contention argument does not apply at all.
> - *"keeps the speech models co-resident with the models they share memory pressure with"* — under a split they are not co-resident with the reasoner.
>
> **Decision 4 nevertheless stands**, on grounds that hold under either topology: it is consistent with the uv-venv runtime pattern ADR-034 established, and there is no in-process Swift MLX path, so a sidecar is what hosting MLX speech models means. Recorded because a reader reopening the split will otherwise find a decision whose every stated reason has lapsed.
>
> *(The Apple Silicon constraint first recorded in this note has been promoted to **Decision 5** below — it is normative content and belongs in the decision set, not in an amendment.)*

## Context

`home/PRD.md` specifies a native macOS app that supervises the Mac-side dmon stack, hosts a realtime
voice loop over a Bluetooth headset, and is the machine iOS clients reach over Tailscale. Its stated
optimisation target is time from launch to a spoken exchange.

`frontends/Dmon.Network` already implements the ADR-012 gateway (Kestrel on `http://127.0.0.1:5500`,
`/ws`, device-key auth, per-session core spawning, attach/replay/resume, heartbeats, TTLs, a
concurrency cap). `core/Dmon.Runtime` already implements core launching, resolution, version
negotiation and RPC correlation — in C#.

`daemon/Daemon.App` ("dmonium") is an existing ~2k-line Swift/SwiftUI macOS app that supervises
`Dcal`, `Dmail` and `Dmon.Network`, runs Tailscale/calendar/mail/egress monitors, and presents a
window-primary dashboard. It has no `.app` bundle and no Xcode project (`swift build` only;
`release.yml` hand-assembles an unsigned bundle).

The monorepo's bucket set is fixed exhaustively by ADR-025 D2 and ADR-028, and mirrored as a testable
standing requirement in `openspec/specs/monorepo-layout/spec.md`. `home/` is not in it — hence this
ADR.

The Product Owner has flagged a possible future topology: `dmon-home` and the back-end
(`Dmon.Network`, the mlx runtimes, the speech sidecar) running on separate physical machines on the
same LAN — the host on a small always-on Mac, the models on a larger machine elsewhere. This ADR does
not design for that split; it is recorded here only so that the gateway-client decision below
(Decision 2) does not foreclose it by treating loopback as an assumption rather than a configuration
value (PRD §7.4).

## Decision

1. **`home/` is a first-class top-level monorepo bucket.** It holds the `dmon-home` macOS host
   product: the product's requirements document (`home/PRD.md`), the XcodeGen manifest, the app
   target, and the host's local Swift packages. It contains **no .NET projects** and therefore carries
   **no `.slnx`** (consistent with ADR-025's rule that an area with no C# members has no solution, and
   that a memberless role bucket has no directory — the bucket materialises with its first member,
   which is the requirements document).
   Rationale for `home/` over the alternatives: `frontends/` is reserved for processes that *are*
   dmon-protocol surfaces (Terminal, Network, Desktop) — `dmon-home` is a *client* of one, and
   `frontends/` is otherwise all .NET; `daemon/` holds the Daemon product's *composition* (ADR-028
   D1), and filing the successor beside the thing it replaces (Decision 3) would obscure the
   retirement. `home/` names what the product is: the at-home surface that hosts the stack.
   **Amends ADR-025 D2 and ADR-028 D1.**

2. **`dmon-home` is an ADR-012 gateway client, not an ADR-003 stdio host.** It connects over a
   WebSocket to a **configured gateway endpoint** — loopback in the co-located deployment,
   but not assumed to be: the endpoint is configuration, not a hard-coded assumption (PRD §7.4) — and
   speaks the same `gw` control frames a remote iOS client speaks
   (`create`/`created`/`createRejected`/`attach`/`attached`/`ack`/`ping`/`pong`). It does **not**
   speak ADR-003 stdio to `dmon-core` and does **not** spawn core processes. Rationale, in order of
   weight:
   1. Speaking stdio directly would mean reimplementing `Dmon.Runtime` in Swift — launcher, resolver,
      version negotiation, transport, RPC correlation, session lifecycle.
   2. **ADR-003 commands carry no session identifier.** Session is ambient per-core-process state
      mutated by `session.create`/`session.load`, so a single stdio channel cannot serve the local
      voice loop and a remote iOS client concurrently. The gateway already solves this by spawning
      one core per session; bypassing it means building a second, competing core-spawning path.
   3. `attach`/`attached` already carry `lastSeq`, `headSeq` and `generation`, so replay and resume
      after a dropped connection come free locally.
   4. The local and remote clients become the same client, over the same protocol, tested once.

   The cost is one WebSocket hop — loopback in the co-located deployment. The transport
   sits behind a swappable Swift protocol, and no code above it may reference a concrete WebSocket
   type. Its day-one payoff is testability: an in-memory conformer drives the handshake and turn flow
   in tests with no network involved. Its second payoff is topology: a remote or TLS-secured
   transport is a conforming type, not a rewrite. This matters because PRD §2.1's stated fallback — a
   Swift port of `Dmon.Runtime` and a direct stdio core — is contingent on co-location: a stdio core
   cannot be spawned on a machine the host is not running on. That **strengthens** this decision
   rather than weakening it — being a gateway client, behind a swappable transport, is what makes a
   future split deployment possible at all.

3. **`dmon-home` supersedes `dmonium`; `dmonium` is retired at parity by a later change.** The new
   product is named **`dmon-home`**, bundle id **`ai.daemonic.dmon-home`**, app target
   `DmonHomeApp`. `ai.daemonic.dmonium` retires with dmonium's code rather than being inherited —
   distinct bundle ids let both apps be installed side by side during the parity period without
   contending over Keychain items, login-item registration or TCC grants, which is what makes a
   staged retirement safe. **Parity is explicitly the superset**, not PRD §2.2's three children:
   dmonium's `DaemonController.bootstrap()` starts and health-registers seven — Network, Dcal,
   Dmail, Tailscale, Calendar Sync, Mail, Egress Endpoint — plus `Keychain` and `LoginItemManager`;
   `dmon-home` must cover all of those **plus** the mlx reasoner, the mlx triage head and the speech
   sidecar before dmonium is deleted. Until that change lands, `daemon/Daemon.App` remains a
   supported, building, shipping member of `daemon/`, and ADR-028 D1/D2/D6 remain in force for it.
   This ADR records the supersession *direction* and the naming; it does **not** itself remove
   dmonium. **Amends ADR-028 D2** (and prospectively ADR-025 D10 / ADR-028 D6 for artifact sources,
   effective when `dmon-home` gains a release artifact — it has none yet, by design).

4. **STT/TTS run in a Python/mlx sidecar; VAD stays host-side.** Speech-to-text and text-to-speech
   are hosted in a supervised Python/mlx sidecar process reached over a socket, not in-process in
   Swift via sherpa-onnx. Rationale: two MLX runtimes competing for unified memory is worse than one
   socket hop, and the sidecar keeps the speech models co-resident with the models they share memory
   pressure with — consistent with the uv-venv runtime pattern ADR-034 established. This settles
   hosting for **STT/TTS only**: Silero **VAD** runs host-side (ONNX Runtime, CoreML execution
   provider), because round-tripping to a backend to decide whether someone is speaking is not
   acceptable latency-wise.

   > **Both rationales above are specific to the co-located deployment** — see the 2026-08-02
   > amendment at the top of this ADR. Under a split back-end the memory-contention argument does not
   > apply at all (only one MLX runtime is on the host) and the co-residency argument is false. The
   > decision stands on the uv-venv pattern and the absence of an in-process Swift MLX path.

5. **`dmon-home` targets Apple Silicon (`arm64`) only.** The app is built for `arm64` and produces no
   `x86_64` slice. MLX is built on Metal and unified memory and has **no Intel path**; since
   `dmon-home` runs the Decision 4 speech sidecar on its own machine, an Intel build could be
   compiled but never function — an untested slice that falsely implies Intel support. This binds the
   XcodeGen manifest of Decision 1 and the app target of Decision 3, and it makes any future
   `dmon-home` release artifact arm64-only for release-matrix purposes (ADR-035 D3/D7).

   Note that **ADR-034 does not itself record this constraint** — it says nothing of arm64, Metal or
   unified memory — so the requirement that makes Intel impossible was, until this ADR, written
   nowhere a reader or an agent would find. It is stated here because `dmon-home` is the first
   product whose build had to act on it; recording it against the MLX runtime itself remains
   outstanding.

## Consequences

- **`home/` is real but minimal.** No `.slnx`, no C# — a pure-Swift bucket, so `Everything.slnx` and
  the "core ⇒ all" path-filtered CI rule (ADR-025 D9) are unaffected by its existence.
- **The local voice loop and the iOS client share one code path.** Both reach a session only through
  the gateway's `gw` control-frame sub-protocol; a protocol fix or fencing/dedup guarantee benefits
  both without duplicated client logic.
- **Two Swift macOS apps coexist during the parity period.** `daemon/Daemon.App` keeps shipping,
  unmodified, until a later change proves `dmon-home` covers its full surface and retires it.
  Distinct bundle ids keep their Keychain, login-item and TCC state from colliding.
- **The speech sidecar is a future supervised child, not code landing here.** This decision only
  fixes where STT/TTS will live so the packages that need it are shaped for a socket client from the
  start.
- **`dmon-home` will never run on an Intel Mac**, and its release artifact — when it gains one — is
  single-architecture. A machine hosting `dmon-home` under a split back-end must also have the memory
  for the speech models, since Decision 4's sidecar follows the host rather than the reasoner.

## Relationship to other ADRs

- **ADR-025** — *Amends D2* (adds `home/` to the bucket set) and *prospectively D10* (artifact
  sources, once `dmon-home` ships a release artifact). All other ADR-025 decisions — per-area
  `.slnx`, intra-repo `ProjectReference`, hybrid openspec roots, path-filtered CI — apply to `home/`
  unchanged, except that `home/` carries no `.slnx` at all (Decision 1).
- **ADR-028** — *Amends D1* (bucket membership: `daemon/` is no longer the only new bucket since
  ADR-025), *D2* (`dmonium`'s placement, product name and bundle id are superseded going forward by
  `dmon-home`/`ai.daemonic.dmon-home`), and *prospectively D6* (artifact source, once `dmon-home`
  ships). `daemon/`, `services/`, the `dcal` rename, and Swift-in-repo (**ADR-028** Decisions 3–5, 7)
  are untouched; `daemon/Daemon.App` keeps building and shipping until its retirement change lands.
- **ADR-012** — *Builds on, unchanged.* `dmon-home` is a conforming client of the existing `gw`
  control-frame sub-protocol (create/attach/replay/resume); no wire-string, frame-shape, or transport
  decision changes.
- **ADR-003** — *Builds on, unchanged.* `dmon-home` deliberately does not speak this contract
  directly; it reaches it only through the gateway.
- **ADR-034 / ADR-036** — *Builds on, unchanged.* The mlx runtime pattern and the loopback-vs-
  non-loopback device-key posture are consumed as-is by the speech sidecar and the device-key
  authentication the host will perform against `Dmon.Network`.
- **ADR-024 / ADR-035** — *Builds on; ADR-035's package→family map prospectively amended.*
  `dmon-home`, once it ships an artifact, joins the app-artifact release family as an independently
  versioned member, exactly like the Gateway daemon and dmonium's `.app`. Per **Decision 5** that
  artifact will be **arm64-only**, so ADR-035 D3/D7's map gains a single-architecture member when it
  lands. No ADR-035 decision text changes; the entry does not exist yet.
