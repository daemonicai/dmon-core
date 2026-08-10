> **Note:** This file is licensed under the same terms as the repository. Written 2026-07-31.

# PRD — Dmon Mac Host

**Status:** draft for implementation
**Target:** native macOS app (Swift), single Xcode project
**Protocol:** ADR-003 (JSONL/stdio) via ADR-012 gateway, wire version `0.2`

---

## 1. What this is

A native macOS application that acts as the host for the whole Mac-side dmon stack: it supervises the model and gateway processes, provides a realtime voice interface over a Bluetooth headset, and is the machine that iOS clients reach over Tailscale.

It replaces two things that will not be built:

- the Avalonia desktop client (`frontends/Dmon.Desktop`) on macOS
- a separate menu bar app

### Why it exists

Testing the voice stack currently requires installing an iOS build and reaching the Mac over Tailscale. That loop is long enough to be a genuine barrier to iterating. The goal is a single application that can be launched, spins up everything it needs, and lets a conversation happen immediately.

Optimise for **time from launch to a spoken exchange**. Where a design choice trades startup friction against architectural elegance, take the one with less friction.

---

## 2. Architecture

### 2.1 Key decision: the Mac host is a gateway client, not an stdio host

`frontends/Dmon.Network` already implements the ADR-012 gateway: Kestrel on `http://127.0.0.1:5500`, WebSocket at `/ws`, device-key auth, per-session core process spawning, attach/replay/resume, heartbeats, TTLs and a concurrency cap. `core/Dmon.Runtime` already implements core process launching, resolution, protocol version negotiation and RPC correlation.

The Mac host therefore **connects to `Dmon.Network` over loopback WebSocket**, using the same `gw` control frames the iOS client uses. It does not speak ADR-003 stdio to `dmon-core` directly.

Rationale:

- Speaking stdio directly means reimplementing `Dmon.Runtime` in Swift — launcher, resolver, version negotiation, transport, RPC correlation, session lifecycle.
- ADR-003 commands carry no session identifier. Session is ambient per-core-process state mutated by `session.create` / `session.load`. A single stdio channel cannot serve the local voice loop and a remote iOS client concurrently. The gateway solves this by spawning one core per session; bypassing it means building a second, competing core-spawning path.
- `attach` / `attached` already carry `lastSeq`, `headSeq` and `generation` — replay and resume after a dropped connection. That is worth having locally too, and it is free.
- The local and iOS clients become the same client, over the same protocol, tested once.

The cost is one loopback WebSocket hop. After upgrade it is a framed socket on the same machine; it is not meaningfully "HTTP indirection".

> **Reversibility:** if the hop proves to be a problem, the fallback is a Swift port of `Dmon.Runtime` and a direct stdio core. Nothing in this PRD depends on the gateway other than the `RpcClient` package's transport, which should be written behind a protocol so the transport can be swapped without touching callers.

### 2.2 Process tree

```
Dmon Mac Host (Swift, .app)
├─ mlx model server(s)                 supervised, reattach-first
│    ├─ reasoner    (Gemma 4 26B-A4B)  load on demand, idle-unload
│    └─ triage head (Gemma 4 E2B/E4B)  resident
├─ speech service (Parakeet STT + TTS) supervised
└─ Dmon.Network (gateway, :5500)       supervised
     └─ dmon-core  (one per session)   spawned by the gateway, not by us

  Mac Host ──ws──> 127.0.0.1:5500/ws
  iOS      ──ws──> tailscale serve ──> 127.0.0.1:5500/ws
```

The host supervises three children. It does not supervise `dmon-core`; the gateway owns those.

### 2.3 Swift package layout

All code lives in local SPM packages inside the one Xcode project. The app target is a thin shell. This keeps the `.xcodeproj` nearly static (agents mangle it), and lets everything except the audio engine and UI be tested headlessly.

| Package | Responsibility |
|---|---|
| `AudioEngine` | Core Audio I/O, route management, VAD, playback queue, barge-in |
| `Speech` | STT/TTS client; endpointing and turn segmentation |
| `Supervisor` | Child process lifecycle, health checks, reattach, teardown |
| `GatewayClient` | WebSocket transport, `gw` control frames, ADR-003 command/event codec |
| `Directedness` | Device-directed classification and memory gating |
| `Power` | Assertions, App Nap avoidance, sleep/wake observation |
| `DmonHostApp` | Menu bar UI, transcript view, settings, wiring |

---

## 3. Scope

### In scope (MVP)

- Supervise and health-check mlx, speech and gateway processes
- Connect to the gateway, create/attach a session, submit turns, render streamed events
- Continuous voice over a Bluetooth headset: capture → VAD → STT → turn submit → TTS → playback
- Barge-in: interrupt playback and abort the in-flight turn
- Device-directedness classification gating both response and memory persistence
- Headset button control (interrupt, mute)
- Menu bar UI: status, live transcript, model residency, child process health, log pane

### Out of scope

- Echo cancellation. The headset handles it in HFP. See §4.1.
- Wake word. VAD plus directedness classification replaces it. See §5.
- Waking a sleeping Mac remotely. **Pinned** — see §7.3.
- Windows or Linux. This is macOS only and may use any Apple API freely.
- App Store distribution. The app will be unsandboxed.

---

## 4. Audio

### 4.1 Bluetooth profile

Target device is a Shokz bone-conduction headset. It stays connected while the user moves between rooms; the MacBook sits on the desk.

**Hold the device in HFP for the entire session.** Bluetooth Classic cannot provide A2DP-quality output and an open microphone simultaneously. Opening the mic forces a profile switch to HFP with an audible dropout of roughly 0.5–1s and a real risk of clipping the first word. A permanently-open mic means permanent HFP: consistent 16kHz (mSBC) or 8kHz (CVSD fallback) both ways, headset-side echo cancellation always active, and no switching artefacts. 16kHz mono is what Parakeet wants anyway.

Log which codec negotiated at session start. CVSD fallback will noticeably degrade STT accuracy and needs to be visible rather than mysterious.

Because the headset performs echo cancellation in HFP, no host-side AEC is required. Do not implement one.

### 4.2 Device selection and route loss

- Select the headset **by device UID**, not by following the system default device. The user often still has the headset on after video calls; another app may own the default route.
- Register a Core Audio property listener on the default device, and observe `AVAudioEngineConfigurationChangeNotification`.
- On route loss, **pause and hold**. Never fall through to built-in mic and speakers. The failure this prevents: the daemon speaking aloud into an empty room while the MacBook's own microphone listens to the house.
- Surface route state in the menu bar. Resume automatically when the headset returns.

### 4.3 Render callback constraints

These are non-negotiable and must be honoured in any generated code:

- The render callback is a `@convention(c)` function pointer. No Swift runtime entry, no ARC traffic, no allocation, no locks, no logging, no `Task`, no `os_log`.
- It copies samples into a single-producer / single-consumer lock-free ring buffer and returns. Nothing else.
- All downstream work — VAD, framing, STT dispatch, gateway I/O, UI — happens on a consumer task reading from the ring buffer.
- `@unchecked Sendable` and `nonisolated(unsafe)` are permitted at the ring buffer boundary, where a safety property is being asserted that the compiler cannot see. Every other use requires a justification line in `journal.md`. If these start appearing elsewhere, a concurrency diagnostic has been suppressed rather than a problem solved.

Violations of these rules produce code that compiles, runs, and drops buffers under load in a way that looks like backend latency. There is no automated test for this; the constraint is the defence.

### 4.4 VAD and endpointing

- Silero VAD via ONNX Runtime, CoreML execution provider, 32ms frames.
- Endpoint on trailing silence. Threshold **must be user-configurable at runtime** and will need tuning by feel — too short cuts off mid-thought pauses, too long makes every response feel sluggish. Start at 600ms.
- VAD runs host-side. Never round-trip to the backend to decide whether someone is speaking.

### 4.5 Playback and barge-in

- TTS output is chunked and streamed. Playback begins on the first chunk, not on completion.
- Barge-in must be able to: stop playback within one buffer period, flush the pending playback queue, and issue `turn.abort` to the gateway.
- Barge-in triggers: headset button single press, or VAD-detected speech during playback that the directedness classifier accepts.

### 4.6 Self-trigger suppression

Independently of headset AEC, maintain a short rolling window of text handed to TTS. If an incoming transcript substantially matches recently-spoken text, discard it silently. Bone conduction has a structure-borne path to a microphone mounted on the same frame, which acoustic echo cancellation is not designed for. This check is cheap and prevents a runaway loop where the daemon answers itself.

### 4.7 Headset button

Via `MPRemoteCommandCenter`, registered as the active now-playing target.

| Gesture | Action |
|---|---|
| Single press | Interrupt: stop playback, flush queue, `turn.abort` |
| Double press | Mute toggle |

- Mute must stop feeding the pipeline at the capture stage, not discard downstream. It is a privacy control, not a UI state.
- **Distinct earcons for mute-on and mute-off.** The user has no visual channel while away from the desk. The worst failure in the system is muting, forgetting, and talking to a daemon that is not listening. Consider a periodic quiet reminder tone during long mutes.
- Re-establish the now-playing target after audio route changes and focus changes. Any other app playing audio can steal it, after which the button silently stops working.

---

## 5. Device-directedness and memory gating

The user lives alone, so there is no crosstalk or diarisation problem. There is still an addressing problem: VAD detects speech, not speech intended for the daemon. Muttering at a stack trace, reading aloud, singing, a podcast in the next room, and one half of a phone call all reach the microphone.

### 5.1 Classification

- VAD opens the gate; Parakeet transcribes; the **triage head classifies addressed / not addressed** before anything reaches the reasoner.
- Transcribing ambient speech is nearly free — Parakeet 0.6B TDT runs at a small fraction of realtime — so classify on text, not audio.
- **Feed the classifier conversational state, not just the transcript.** At minimum: seconds since the last daemon turn, whether that turn ended in a question, and whether a turn is currently open. "What about Tuesday?" is obviously addressed ten seconds after the daemon proposed times and obviously ambient an hour later. A stateless classifier will be weakest on exactly the utterances that matter.

### 5.2 Uncertainty

When the classifier is uncertain:

- **Only query when there is positive evidence** — recent conversational context, the daemon's name, direct second-person address. Cold ambient speech with no supporting signal is dropped silently and never queried.
- **Signal uncertainty non-verbally**: a soft earcon, not a spoken "sorry, are you talking to me?". It can be answered or ignored, and costs a fifth of a second rather than a sentence. Reserve speech for when the classifier is confident enough to just answer.

### 5.3 Memory gating

Response and persistence are separate gates with different error costs. Responding to muttering is briefly annoying and self-correcting. Writing muttering into episodic memory is silent, permanent, and compounds.

- Persistence is gated on **exchange completion**: classified as addressed, daemon responded, user did not immediately contradict.
- A turn that receives "no, not you" persists nothing but the negative label.
- **Unaddressed transcripts are discarded immediately and never written to disk.** This is the default behaviour that must be implemented deliberately, because the naive implementation keeps everything.

### 5.4 Correction as training data

"No, I wasn't talking to you" is a clean, consented, naturally-occurring negative example. Log transcript, state features and label — nothing else. Over several weeks this yields a real confusion matrix for the directedness head without harvesting ambient speech.

---

## 6. Supervision

The host is effectively an init system with a UI attached. This is likely more specification surface than the audio engine.

### 6.1 Reattach-first

Health-check the known endpoint. If it answers, **adopt the existing process**. Only spawn if it does not.

This matters most for mlx. A crash in the audio engine takes down anything spawned as a child with inherited pipes, and reloading a 26B model costs tens of seconds and gigabytes of disk reads. During audio development that will happen frequently. Because mlx is reached over a socket, it can outlive the host; make it do so.

### 6.2 Per-child requirements

| Child | Transport | Reattach | Notes |
|---|---|---|---|
| mlx reasoner | socket | yes | on-demand load, idle-unload timer |
| mlx triage | socket | yes | resident |
| speech service | socket | yes | STT + TTS |
| gateway | loopback HTTP | yes | health check `127.0.0.1:5500` |

For each: startup ordering, health check with timeout, crash detection with exponential backoff, graceful shutdown in dependency order, and **process-group kill on exit**. Orphaned mlx processes holding unified memory will cause the next launch to fail allocation for no visible reason.

### 6.3 Model residency

An always-resident companion cannot hold 20GB+ all day on a working machine. Keep the triage head resident; load the reasoner on demand with an idle-unload timer. **Surface current residency in the UI** — otherwise latency will be debugged without knowing whether a cold load was just paid.

### 6.4 Log pane

Stream child stdout/stderr into a viewable pane in the app. For a harness this is most of the debugging value, and it is cheap.

---

## 7. Power and availability

### 7.1 App Nap

A menu bar app with no visible windows is the canonical App Nap target: coalesced timers, dropped priority. Hold:

```swift
ProcessInfo.processInfo.beginActivity(
    options: [.userInitiated, .idleSystemSleepDisabled],
    reason: "dmon gateway serving")
```

while the gateway is enabled; release when disabled. Do not use `LSAppNapIsDisabled` — it is unconditional.

### 7.2 Sleep

- `pmset -c sleep 0` covers idle sleep on charger.
- A `PreventUserIdleSystemSleep` assertion does **not** survive lid close. Clamshell sleep is unavoidable without external power and display. "MacBook shut in a bag" is not a supported state.

### 7.3 Remote wake — pinned, not in scope

Tailscale is a Layer 3 overlay; Wake-on-LAN is a Layer 2 magic packet. A sleeping Mac is not running `tailscaled` and cannot be woken over the tailnet. Waking requires a device already awake on the same physical LAN (a Raspberry Pi is available for this). Even then, macOS WoL produces a **darkwake** that lapses back to sleep in roughly 30 seconds, which races model loading.

MVP behaviour: the iOS client shows "daemon unreachable" honestly and queues user input for delivery on reconnect. It does not spin.

### 7.4 Host is a config value

The gateway is reached by hostname, not assumed to be local. If the whole stack later moves to an always-on Mac mini, that is a config change rather than a rewrite. iOS clients should target a **stable name that is not the Mac's own** so a future front door — the Pi, or a different host — is a DNS change and not an app update.

---

## 8. Security

`Dmon.Network` already enforces the relevant policy; do not weaken it.

- Gateway binds **loopback only**. `tailscale serve` fronts it. Wildcard binds are rejected unconditionally by `NetworkBindPolicy`. Do not set `AllowNonLoopbackBind`.
- Serving via `tailscale serve` also provides a valid TLS certificate on the `*.ts.net` name, which satisfies iOS App Transport Security without exception plists.
- The Mac host is a gateway client and needs its own **device key**, provisioned into `~/.dmon/network/devices.json` like any other client.
- **Tag request provenance.** A request arriving over Tailscale from a phone is not necessarily the same trust level as one from the machine holding the models. Even though the gateway is not a policy layer, stamp origin onto the request so `AbilityRegistry.ForScope` can decide. Cheap now, awkward to retrofit once the system is ambient.

---

## 9. Open questions

**Q1 — Metal from a background context.** If the supervisor is later split into a `LaunchDaemon` so it survives logout and reboot, it runs outside the GUI session, where Metal/GPU initialisation has historically been unreliable. **Test whether `mlx_lm` initialises Metal from a daemon context before committing to that split.** A `LaunchAgent` (user session, requires login) sidesteps it entirely. For the MVP the app is foreground-launched and this does not bite, but it constrains the always-on story.

**Q2 — Triage endpoint access.** Device-directedness classification must happen before a turn is submitted, so the host needs a direct line to the triage model rather than going through the gateway and core. Confirm whether the host calls the mlx endpoint directly, or whether a lightweight classification path should be exposed by the speech service.

**Q3 — Gateway implementation completeness.** `ControlFrames.cs` carries "Group 3/5/6" annotations; `generation` is "issued here but not enforced until Group 6". Establish which groups are implemented before relying on dedup or fencing semantics.

**Q4 — Speech service hosting.** STT/TTS can run in the existing Python/mlx sidecar, or in-process in Swift via sherpa-onnx (which has Swift bindings, Parakeet TDT, Silero VAD and several TTS families). In-process removes a hop and a supervised child; the sidecar keeps models co-resident in one MLX process on unified memory. **Recommendation: sidecar**, on the grounds that two MLX runtimes competing for unified memory is worse than one socket hop.

---

## 10. Phases

**Phase 0 — Foundations.** Xcode project with SPM package skeleton. Supervisor with reattach-first health checks. Log pane. Verify a supervised gateway comes up and answers on `:5500`. No audio.

**Phase 1 — Text loop.** `GatewayClient`: WebSocket, `create` → `created` → `attach` → `attached`, `turn.submit`, render `messageDelta` / `turnEnd`. Typed input in the menu bar UI. This proves the protocol independently of audio.

**Phase 2 — Audio out.** TTS playback of daemon responses. Route selection by UID, route-loss pause-and-hold, chunked streaming playback. Still typed input.

**Phase 3 — Audio in.** Capture, ring buffer, Silero VAD, endpointing, STT, turn submission. Full voice loop, permanently open mic.

**Phase 4 — Control.** Headset button, earcons, mute, barge-in with `turn.abort`, self-trigger suppression.

**Phase 5 — Directedness.** Classification with state features, uncertainty earcon, memory gating, correction logging.

Phases 2 and 3 are the ones with no automated test coverage available. Expect to evaluate them by listening.

---

## 11. Notes for the implementing agent

- **The user will not read most of the generated code.** Prefer clarity and small surfaces over cleverness. Anything unusual belongs in `journal.md`.
- **Nothing in the audio path can be verified automatically.** Correctness of AEC, buffer boundaries, clipping and barge-in timing is judged by ear. Get a runnable app with a log pane and a waveform view early; the feedback loop is a human listening.
- Define the Xcode project with **XcodeGen (`project.yml`)** or Tuist. Do not hand-edit `.xcodeproj`.
- **Disable App Sandbox.** It blocks spawning a Python interpreter from `/opt/homebrew` and reading multi-gigabyte model files. This is correct for a local tool and means no App Store distribution, which is not wanted.
- `NSMicrophoneUsageDescription` must be in the app's `Info.plist`, and the app must be a proper `.app` bundle for TCC to prompt. Without it the microphone silently returns silence and no prompt appears. **Verify the permission prompt appears before writing any audio code.**
- Wire protocol version is `0.2`. Host and core are compatible when `Major.Minor` match. Use `WireSerializerOptions.Default` semantics: camelCase, out-of-order discriminators tolerated, nulls omitted.
- Frames carrying a `gw` field are gateway control frames. Frames without one are ADR-003 commands or events, forwarded byte-unchanged.
