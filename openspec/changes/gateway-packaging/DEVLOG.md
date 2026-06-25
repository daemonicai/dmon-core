# DEVLOG — gateway-packaging

Packaging fix + full `Gateway`→`Network` rebrand. 28 tasks, 10 groups. Branch `change/gateway-packaging`.

## Pinned facts (read before planning any block)

- **Wire/contract strings are NOT renamed.** Control-frame strings (`create`/`created`/`attach`/`sessionId`/`agent`), the `gw` discriminator, and the shared protocol namespace `Dmon.Protocol.Gateway` (a separate contract package, consumed via `using Dmon.Protocol.Gateway;`) stay as-is. The rebrand is host-side only (ADR-012/ADR-015 hold).
- **Runtime config/on-disk strings still say "Gateway" after Block 1 — deliberately.** `NetworkOptions.SectionName = "Gateway"`, `GetSection("Gateway")`, `[dmon-gateway]` console log prefixes in `Program.cs`, and the `.dmon/gateway` device-key store directory were left untouched by Block 1 (pure type/namespace rename). **Decide explicitly in a later block** whether these become "Network"/`.dmon/network` — they are the config contract / on-disk seam, not C# identifiers. Not yet assigned to a task; flag if a block needs them.
- **Capability id `remote-session-gateway` is retained** (internal identifier, high cross-ref churn) — terminology sweep inside the spec only (task 8.1), id/folder unchanged.
- **Group 4 packaging hazard (architect's forward note):** the version-skew guard lives in `Directory.Build.props` `CheckProtocolVersionSkew` target (~lines 64–96), which fires for any `IsPackable=true` project and would reject `Dmon.Network`'s independent version. Task 4.4's carve-out must edit that target, not just spec text. `ndmon` is an app artifact, independently versioned, EXEMPT from the protocol-keyed `Major.Minor` gate (ADR-024).
- **Out of scope / do not edit:** `openspec/changes/archive/**`; the active `daemon-scheduler` change's stale `Dmon.Gateway` references (flag at its apply, don't edit here).
- **Gates:** `make build` (0-warn, TreatWarningsAsErrors), `env -u MEKO_API_KEY make test` (avoids live-Meko smoke hang), `openspec validate gateway-packaging --strict`. dmonium blocks (Group 6) add `swift build -c release --package-path daemon/Daemon.App` + `swift test`.

## Block 1 — tasks 1.1–1.3, 2.1–2.2, 3.1–3.2 (DONE, committed)

Mechanical rename of the host project. `git mv frontends/Dmon.Gateway → frontends/Dmon.Network` (csproj, `RootNamespace`/`AssemblyName`→`Dmon.Network`, `InternalsVisibleTo`→`Dmon.Network.Tests`); 7 `Gateway*`-named source files renamed (`NetworkBindPolicy`, `NetworkConnectionEndpoint`, `NetworkDeviceKeyPaths`, `NetworkOptions`, `NetworkProfilePaths`, `INetworkConnection`, `WebSocketNetworkConnection`); all `namespace Dmon.Gateway*`→`Dmon.Network*` across 20 production sources; `test/Dmon.Gateway.Tests → test/Dmon.Network.Tests` (csproj repathed; 3 `Gateway*` test classes renamed to `Network*`); `frontends.slnx` + `Everything.slnx` repathed.

- **Kept `OutputType=Exe`, `IsPackable=false`** — no packaging yet (Group 4 owns it).
- Wire shape byte-identical; `ByteUnchangedForwardingTests`/`ControlFrameTests` green.
- Reviewer: clean pure rename, approved, no blockers.
- Gates: build 0-warn, test all green (Dmon.Network.Tests 208/208), validate --strict ✓.
- Cosmetic only: stale `Dmon.Gateway.*` files linger in git-ignored `obj/` until next `make clean`.
