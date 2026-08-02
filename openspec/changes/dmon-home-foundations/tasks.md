## 1. ADR-037 and the `home/` bucket

- [x] 1.1 Write `docs/adrs/ADR-037-home-bucket-and-dmon-home.md` (status **Accepted**), following the existing ADR format. Decisions: (D1) `home/` is a first-class top-level monorepo bucket holding the `dmon-home` macOS host product — **amends ADR-025 D2** and **ADR-028 D1** (bucket set); (D2) the Mac host is an ADR-012 **gateway client**, not an ADR-003 stdio host, with the transport behind a swappable abstraction; (D3) `dmonium` (`daemon/Daemon.App`) is **superseded** by `dmon-home` and retired at parity by a later change — **amends ADR-028 D2** (placement and the `ai.daemonic.dmonium` bundle id) and **ADR-025 D10 / ADR-028 D6** (artifact sources) prospectively, naming the new product `dmon-home` / `ai.daemonic.dmon-home`; (D4) STT/TTS run in a Python/mlx **sidecar**, while Silero VAD stays host-side. Builds on ADR-012, ADR-034 and ADR-036.
- [x] 1.2 Add the ADR-037 row to the ADR table in `CLAUDE.md` and a summary entry to the `adr-index` skill (`.claude/skills/adr-index/`).
- [x] 1.3 Create the `home/` bucket directory with the product's top-level layout. Confirm no `home.slnx` is created and that no `.slnx` references anything under `home/`. (Satisfies the `monorepo-layout` "Swift package is excluded from the .NET solutions" and "The macOS host lives in the home bucket" scenarios. Note: the `dmon-home` requirement "The macOS host lives in the `home/` bucket" is **not** fully satisfied here — the bucket's only member at this point is `home/PRD.md`; the XcodeGen manifest, app target and Swift packages arrive at 2.1–2.2, which completes it.)

## 2. Xcode project and Swift package skeleton

- [x] 2.1 Add a checked-in XcodeGen `home/project.yml` defining the `DmonHomeApp` application target (bundle id `ai.daemonic.dmon-home`, macOS 14+). Do **not** commit a hand-edited `.xcodeproj`; confirm the project regenerates from the manifest alone. (Satisfies "The Xcode project is generated, never hand-edited".)
- [x] 2.2 Create the local Swift packages `Supervisor`, `GatewayClient` and `Power`, each with a test target, and wire them into the app target. Do **not** create `AudioEngine`, `Speech` or `Directedness` — they are created by the change that first puts code in them (design D3). (Satisfies "Application logic lives in local Swift packages" and its no-empty-placeholder scenario.)
- [x] 2.3 Add `make` build and test targets for the `home/` packages, named so they do not collide with the existing `daemon-app` targets, and confirm `swift test` runs the package tests headlessly without launching the app.

## 3. App bundle, entitlements, and the microphone gate

- [x] 3.1 Configure the app target to produce a genuine `.app` bundle with **App Sandbox disabled** and `NSMicrophoneUsageDescription` set to a meaningful string in `Info.plist`. (Satisfies "The host ships as an unsandboxed `.app` bundle that can prompt for microphone access" — the bundle and entitlement scenarios.)
- [x] 3.2 Add a minimal microphone authorisation request path plus a UI surface showing the current authorisation status. No capture, no audio engine — only the permission request and its result.
- [x] 3.3 **HUMAN VERIFICATION — do not tick without Product Owner confirmation.** Recipe: run `make dmon-home-app`, then open the built bundle from Finder — `open -R home/.build-xcode/Build/Products/Release/DmonHomeApp.app` reveals it — and launch it by double-clicking (not from a terminal, and not the raw binary inside: TCC keys the grant on the **bundle**). Trigger the authorisation request from the UI. Confirm macOS shows the microphone permission prompt and that it displays the configured usage description. Report the observed prompt text back before ticking. Note: the app is ad-hoc signed, so TCC — which keys a grant on bundle id **plus** cdhash — discards the grant on every rebuild; a fresh prompt each time is expected, and `tccutil reset Microphone ai.daemonic.dmon-home` should not be needed. If no prompt appears at all, that is the failure PRD §11 warns about (silent silence, no prompt) and the bundle or usage string is wrong. (Satisfies "The microphone permission prompt appears".)
- [x] 3.4 Restrict the app target to `arm64` in `home/project.yml` so no `x86_64` slice is produced and `xcodebuild` stops offering an ambiguous destination. Verify with `lipo -archs` (or `file`) against the built executable, and confirm the multiple-matching-destinations warning is gone. Do **not** change `-configuration Release` or `-derivedDataPath home/.build-xcode` — task 3.3's recipe hard-codes the resulting path. (Satisfies "The host targets Apple Silicon only" — both scenarios.)

## 4. Supervision

- [x] 4.1 Define the child descriptor — transport, endpoint, health check, timeout, startup order, adoption policy — so a child is expressed as configuration rather than a bespoke type. Enumerate the full inventory (network gateway, Dcal, Dmail, mlx reasoner, mlx triage, speech sidecar) and the read-only monitors (Tailscale, calendar sync, mail, egress) as descriptors, marking which are started in this change. (Satisfies "The child model accommodates the full inventory".)
- [ ] 4.2 Implement reattach-first startup: health-check the endpoint, adopt a live process, spawn only when nothing answers. Track whether each child was adopted or spawned. (Satisfies "Children are adopted before they are spawned" — adopt and spawn scenarios.)
- [x] 4.3 Implement bounded health checking: every check carries a timeout, a check exceeding it is recorded as failed without blocking other children, and each child's health is published for the UI. (Satisfies "Each child is health-checked with a bounded timeout".)
- [ ] 4.4 Implement crash detection with exponential backoff between restart attempts, and surface repeated failure rather than retrying silently. (Satisfies "Crashes are detected and retried with exponential backoff".)
- [ ] 4.5 Implement dependency-ordered startup and reverse-ordered graceful shutdown, requesting graceful termination before escalating. (Satisfies "Startup and shutdown follow declared dependency order".)
- [ ] 4.6 Place each **spawned** child in its own process group and kill that group on host exit; leave **adopted** children running. (Satisfies "Spawned children are killed by process group on exit" and "An adopted child outlives the host".)
- [ ] 4.7 Register `Dmon.Network` as the one child started in this change, resolving its executable the way `dmonium`'s `NetworkManager` does (default `~/.dotnet/tools/ndmon`, overridable). Verify a supervised gateway comes up and answers on `127.0.0.1:5500`, and that relaunching the host adopts it rather than spawning a second one.

## 5. Log pane and power assertion

- [ ] 5.1 Stream each supervised child's stdout and stderr into a log pane in the app, attributed by child and retained across a child restart. (Satisfies "Child process output is streamed to a log pane".)
- [ ] 5.2 In the `Power` package, hold a `ProcessInfo.beginActivity` assertion covering user-initiated work and idle system sleep while the gateway is enabled, and release it when disabled. Do **not** use `LSAppNapIsDisabled`. (Satisfies "The host holds an activity assertion while the gateway is enabled".)

## 6. Gateway client — transport and frame codec

- [ ] 6.1 Define the transport abstraction and implement the WebSocket conformer. No type above the abstraction may reference a concrete WebSocket type. Provide an in-memory conformer for tests. (Satisfies "The transport is abstracted behind a protocol".)
- [ ] 6.2 Implement the frame codec: route by the `gw` discriminator, encode/decode `attach`, `attached`, `ack`, `create`, `created`, `createRejected`, `ping`, `pong`, and pass frames without a `gw` field through as ADR-003 events. Match the shapes in `core/Dmon.Protocol/Gateway/ControlFrames.cs`. Serialisation is camelCase, tolerates an out-of-position discriminator, and omits nulls. Cover each with a decode test. (Satisfies "Frames are routed by the gateway discriminator" — all four scenarios.)
- [ ] 6.3 Answer `ping` with `pong` so the network host's heartbeat does not reap the connection.
- [ ] 6.4 Implement wire-version declaration and `Major.Minor` compatibility checking against version `0.2`, surfacing a mismatch as an actionable error naming both versions. (Satisfies "Wire protocol compatibility is checked on connect".)
- [ ] 6.5 Implement device-key authentication: connect without a key when the store is empty/absent on a loopback bind; otherwise present the host's own credential, with the secret held in the Keychain and never written to config or logs. Compute `secretHash` as hex-encoded SHA-256 of the token via CryptoKit, and **pin the exact hex digest of a known token in a test** so any divergence from the C# `DeviceKeyAuthenticator` fails loudly (design risk 2). (Satisfies "The client authenticates with a device key when the store requires it".)

## 7. Gateway client — session lifecycle and turns

- [ ] 7.1 Implement the create→attach handshake: send `create`, await `created`, send `attach` with the returned session id, and record `generation` and `headSeq` from `attached`. (Satisfies "A session is established by create then attach" — the create-then-attach scenario, and "The host reaches its session through the gateway, not through stdio".)
- [ ] 7.2 Surface `createRejected` as an actionable error carrying its code and message, distinguishable from an ADR-003 error event, and do not attach after a rejection. (Satisfies the rejected-create scenario.)
- [ ] 7.3 Track the highest observed event sequence number and reattach with it after a dropped connection, rendering replayed events without duplicating already-rendered ones. (Satisfies "Reattach resumes from the last observed sequence" — both scenarios.)
- [ ] 7.4 Implement turn submission as an ADR-003 command with a session-unique id, and incremental rendering of message deltas through to turn end. (Satisfies "Turns are submitted and streamed replies rendered incrementally" — both scenarios.)

## 8. UI text loop

- [ ] 8.1 Add a text input that submits a turn to the attached session and a transcript view that renders the streamed reply incrementally. (Satisfies "The host accepts typed turn input and renders streamed replies" — the rendered-reply scenario.)
- [ ] 8.2 Refuse submission when no session is attached, surfacing the unattached state rather than failing silently. (Satisfies the not-attached scenario.)
- [ ] 8.3 Surface supervised-child health and the gateway connection state in the UI alongside the transcript, so a failed turn can be attributed to the right layer.

## 9. Protocol comment correction

- [ ] 9.1 In `core/Dmon.Protocol/Gateway/ControlFrames.cs`, correct the stale annotations: `AttachedFrame`'s "issued here but not enforced until Group 6" and `AckFrame`'s "dedup logic is Group 5". Both are implemented — `CommandAdmission { Accepted, Duplicate }` backs `SessionHandler.TryAdmitCommand`, and `SessionHandler.Attach` evicts, fences and aborts the prior connection. Re-verify against the code before editing. **Comments only** — no behaviour change and no spec delta. Run `env -u MEKO_API_KEY make test` (the live-Meko smoke test hangs ~90s when `MEKO_API_KEY` is set).

## 10. CI, and doc/spec sync

- [ ] 10.1 Add a second macOS CI job in `.github/workflows/ci.yml` for the `home/` packages with its **own** path filter scoped to `home/**`, independent of the existing `Daemon.App` job and untriggered by .NET-area changes. Leave the `Daemon.App` job unchanged. (Satisfies the modified "Swift app is built and tested on macOS" requirement — both scenarios.)
- [ ] 10.2 Update the comment in `.github/area-map.yml` that currently asserts `daemon/Daemon.App/**` is the sole intentionally-unmapped Swift path, so it names both Swift packages.
- [ ] 10.3 Confirm `daemon/Daemon.App` is untouched: its `make` targets, its CI job and its release artifact all still work, and `release.yml` is unmodified. (Satisfies "The existing Swift app is untouched".)
- [ ] 10.4 Run `openspec validate dmon-home-foundations --strict` and `make build` to confirm the tree is green and warning-free.
