## 1. Make `Dmon.Gateway` a packable dotnet tool

- [ ] 1.1 In `frontends/Dmon.Gateway/Dmon.Gateway.csproj`, add `PackAsTool=true`, `IsPackable=true`, a `PackageId`, and `ToolCommandName` such that the installed executable file name is exactly `Dmon.Gateway` (matching `GatewayManager`'s default candidate). Keep `OutputType=Exe`, `TargetFramework=net10.0`.
- [ ] 1.2 Add the package metadata a packable project needs to satisfy the existing `package-publishing` "Package license and metadata" requirement (`PackageLicenseExpression` MPL-2.0 via the central `Directory.Build.props`; confirm authors/repo URL inherited). Do NOT introduce a `dmoncore`/protocol NuGet dependency that would pull the Gateway onto the lockstep line.
- [ ] 1.3 Give the Gateway tool an **independent version** (its own property/cadence), explicitly NOT keyed to `ProtocolVersion.Current`.

## 2. Exempt the Gateway from the protocol-keyed version gate

- [ ] 2.1 Locate the build/release version-consistency + packability enforcement (the check behind `package-publishing` "Protocol-keyed three-part version scheme" / "Only the … projects are packable") and exclude app-artifact dotnet tools (`Dmon.Gateway`) from the `Major.Minor == ProtocolVersion.Current` gate while still allowing it to pack.
- [ ] 2.2 Verify a solution-wide pack still produces only the intended packages (protocol-keyed first-party set + the `Dmon.Gateway` tool), and that `Dmon.Runtime`/internal/test projects remain non-packable.

## 3. `make gateway` target

- [ ] 3.1 Add a `gateway` target to the `Makefile` (build → pack → install the tool to the default dotnet-tools location), parallel to `make daemon-app`. Make re-runs idempotent (update if already installed).
- [ ] 3.2 Confirm that after `make gateway` on a clean checkout, `~/.dotnet/tools/Dmon.Gateway` resolves.

## 4. Verify dmonium resolves and starts the Gateway

- [ ] 4.1 Confirm `GatewayManager.swift` default candidate (`~/.dotnet/tools/Dmon.Gateway`) matches the installed file name exactly (casing included). If `ToolCommandName` cannot produce that exact name, align the Swift default candidate (one-line change) per design D1 caveat — prefer making the package match.
- [ ] 4.2 Human-verify (recipe): on a machine with the tool installed via `make gateway` and no `DMON_GATEWAY_PATH`, launch dmonium and confirm the Gateway health row goes green (process starts) out of the box.

## 5. Docs

- [ ] 5.1 Update `daemon/Daemon.App/README.md` (and optionally add `frontends/Dmon.Gateway/README.md`) noting the Gateway is installed via `make gateway` / `dotnet tool install`, resolves at `~/.dotnet/tools/Dmon.Gateway`, and is overridable with `DMON_GATEWAY_PATH`.

## 6. Validate

- [ ] 6.1 `openspec validate gateway-packaging --strict` passes.
- [ ] 6.2 `make build` / solution pack is clean (no warnings; `TreatWarningsAsErrors`), and `make test` stays green.
