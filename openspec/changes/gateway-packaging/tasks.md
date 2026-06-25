## 1. Rename the host project `Dmon.Gateway` → `Dmon.Network`

- [x] 1.1 `git mv frontends/Dmon.Gateway frontends/Dmon.Network` and `Dmon.Gateway.csproj` → `Dmon.Network.csproj`; set `RootNamespace`/`AssemblyName` to `Dmon.Network`.
- [x] 1.2 Replace `namespace Dmon.Gateway` → `namespace Dmon.Network` across all sources, and rename every `Gateway*` type to `Network*` (`GatewayOptions`→`NetworkOptions`, `IGatewayConnection`→`INetworkConnection`, `WebSocketGatewayConnection`→`WebSocketNetworkConnection`, `GatewayBindPolicy`→`NetworkBindPolicy`, `GatewayConnectionEndpoint`→`NetworkConnectionEndpoint`, `GatewayProfilePaths`/`GatewayDeviceKeyPaths`→`Network*`, etc.) plus all references.
- [x] 1.3 Keep `OutputType=Exe`, `TargetFramework=net10.0`, and the existing `ProjectReference`s to `core/Dmon.Runtime` and `core/Dmon.Core`.

## 2. Rename the test project `Dmon.Gateway.Tests` → `Dmon.Network.Tests`

- [x] 2.1 `git mv test/Dmon.Gateway.Tests test/Dmon.Network.Tests` and the csproj; update its `ProjectReference` to `frontends/Dmon.Network`.
- [x] 2.2 Update namespaces/usings and any `Dmon.Gateway` references in the ~20 test files to `Dmon.Network`; tests stay green.

## 3. Solutions and references

- [x] 3.1 Update `frontends.slnx` and `Everything.slnx` to the renamed project/test paths and names.
- [x] 3.2 Repath any remaining `ProjectReference`/path references to the old `Dmon.Gateway` locations.

## 4. Make `Dmon.Network` an installable dotnet tool (the OOTB fix)

- [ ] 4.1 In `Dmon.Network.csproj` add `PackAsTool=true`, `IsPackable=true`, `PackageId=Dmon.Network`, and `ToolCommandName=ndmon`.
- [ ] 4.2 Add the package metadata a packable project needs (`PackageLicenseExpression` MPL-2.0 via the central `Directory.Build.props`; authors/repo URL inherited). Do NOT add a `dmoncore`/protocol NuGet dependency that would pull the tool onto the lockstep line.
- [ ] 4.3 Give the tool an independent version property, explicitly NOT keyed to `ProtocolVersion.Current`.
- [ ] 4.4 Update the build/release version-consistency + packability enforcement to exempt app-artifact dotnet tools (`Dmon.Network`) from the `Major.Minor == ProtocolVersion.Current` gate while still allowing it to pack. (Without this the pack fails CI.)
- [ ] 4.5 Verify a solution-wide pack produces only the intended packages (protocol-keyed first-party set + the `Dmon.Network` tool); `Dmon.Runtime`/internal/test projects stay non-packable.

## 5. `make network` target

- [ ] 5.1 Add a `network` target to the `Makefile` (build → pack → install to the default dotnet-tools location), parallel to `make daemon-app`; idempotent (update if already installed).
- [ ] 5.2 Confirm that after `make network` on a clean checkout, `~/.dotnet/tools/ndmon` resolves.

## 6. Rebrand dmonium (`daemon/Daemon.App`)

- [ ] 6.1 Rename `GatewayManager.swift`/`class GatewayManager` → `NetworkManager`; update `DaemonController` (`gateway`→`network`, `observeGatewayStopped`→`observeNetworkStopped`, the stable display-order comment, the special-icon-role wiring).
- [ ] 6.2 Change the health component label `"Gateway"` → `"Network"` (the special stopped→red icon role follows the renamed component).
- [ ] 6.3 Change the default resolved path `~/.dotnet/tools/Dmon.Gateway` → `~/.dotnet/tools/ndmon`.
- [ ] 6.4 Rename the config/env key `DMON_GATEWAY_PATH` → `DMON_NETWORK_PATH` (clean break, no alias) in `GatewayManager`/`NetworkManager`, `SettingsView` (load/save map + the "Gateway binary path" field + help text).
- [ ] 6.5 `swift build -c release --package-path daemon/Daemon.App` and `swift test` stay green.

## 7. ADRs

- [ ] 7.1 Write `docs/adrs/ADR-033-<slug>.md` ("Rename the gateway host to `Dmon.Network` / `ndmon`") recording the decision and rationale; status Accepted.
- [ ] 7.2 Add one-line terminology amendment notes to ADR-012, ADR-017, ADR-018, and ADR-028 ("Gateway" → "Network host" / the renamed frontend), referencing ADR-033. No numbered decision is reversed. (If the reviewer judges any "Gateway" usage was load-bearing in a numbered decision → stop-and-ask.)

## 8. Standing spec terminology

- [ ] 8.1 Apply the `remote-session-gateway` delta (this change's `specs/remote-session-gateway/spec.md`): rename "Gateway session-create control frame" → "Network session-create control frame" and sweep "the gateway" → "the network host" terminology across the standing spec prose. Keep the capability id/folder `remote-session-gateway` (internal identifier).

## 9. Docs

- [ ] 9.1 `git mv docs/deploying-the-gateway.md docs/deploying-the-network.md` and sweep its content (host name, `ndmon`, `make network`, `DMON_NETWORK_PATH`); fix inbound links.
- [ ] 9.2 Update `daemon/Daemon.App/README.md`: the host is installed via `make network` / `dotnet tool install` (command `ndmon`), resolves at `~/.dotnet/tools/ndmon`, overridable with `DMON_NETWORK_PATH`; refresh any "Gateway" wording and the source/test file lists.

## 10. Completeness, validation, gates

- [ ] 10.1 Grep gate: no stray `Dmon.Gateway` / `Gateway` / `gateway` / `DMON_GATEWAY_PATH` references remain outside the deliberately-retained capability id `remote-session-gateway` and the archived `openspec/changes/archive/**`. (Note: the active `daemon-scheduler` change's stale references are out of scope — flag, don't edit.)
- [ ] 10.2 `openspec validate gateway-packaging --strict` passes.
- [ ] 10.3 `make build` clean (no warnings; `TreatWarningsAsErrors`), `make test` green, and `swift build`/`swift test` for dmonium green.
- [ ] 10.4 Human-verify (recipe): with the tool installed via `make network` and no `DMON_NETWORK_PATH`, launch dmonium and confirm the **Network** health row goes green (process starts) out of the box.
