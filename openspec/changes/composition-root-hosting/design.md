## Context

ADR-019 (Accepted) collapses three mechanisms — the runtime NuGet downloader, the `config.yaml` active-extension list (reflection-loaded into the Default ALC), and the dynamic `.csx`/`extension.load` tier — into one: the user authors a `Dmon.cs` file-based program and the .NET 10 SDK builds it. This change implements ADR-019 only; agent definitions (ADR-020) and the `compose` permission tier (ADR-021) are separate follow-ups. The `ICoreLauncher`/`ICoreProcess` seam already exists in `Dmon.Runtime` (from the gateway create work) and is the natural place the new launch lands.

## Goals / Non-Goals

**Goals:**
- `dmoncore` is a library with a `DmonHost` hosting surface; `Dmon.cs` is the entry point.
- Extensions are compile-time `#:package` + builder registrations; the entire runtime-load/config/`.csx` machinery is deleted.
- The host builds then runs the core with the build phase kept off the JSONL/stdio wire.
- Launcher precedence `Dmon.cs` > override > prebuilt default; the no-SDK/first-run path still works via the prebuilt default.
- `config.yaml` retained for settings only.

**Non-Goals:**
- ADR-020 agent definitions and the `compose` gate (ADR-021) — **the agent must not be allowed to self-edit `Dmon.cs` until the `compose` tier lands**; this change does not add that capability.
- Changes to the wire contract framing (ADR-003), session storage (ADR-004), or the permission *pipeline* (ADR-002/006) — only the entry point and the extension-acquisition model move.
- Mid-session `.csx` dynamism — replaced by edit-`Dmon.cs`-then-reload (ADR-019 accepted trade-off).

## Decisions

### D1 — Hosting surface as a thin extraction
`DmonHost.CreateBuilder(args).…Build().RunAsync(ct)` wraps today's core bootstrap; `RunAsync()` *is* the existing JSONL/stdio loop. The change is mechanical relocation of `Program.Main` into a library API, not a rewrite of the loop. Builder methods cover provider/model, extension registration, permission mode, and profile.

### D2 — Build-then-`--no-build`-run via `ICoreLauncher` (stdout purity)
`Dmon.Runtime` launches a `Dmon.cs` core in two steps: `dotnet build Dmon.cs` (separate process, stdout/stderr captured — also the incremental staleness gate), then `dotnet run Dmon.cs --no-build` as the stdio child. `--no-build` skips the build phase and implies `--no-restore`, so no MSBuild/restore line can precede the program on stdout. The prebuilt default/override path `dotnet exec`s the assembly directly. This is the single most important correctness property: the build must never share the JSONL channel.

### D3 — Packaging: one library, one prebuilt default (ADR-019 D9)
`dmoncore` ships as a library package; the stock default core is a prebuilt publish of a canonical `Dmon.cs` that references it. "Default" and "authored" are one mechanism at two build-times. **Open:** exactly how the prebuilt default reaches the user with no SDK/network — bundled with the `dmon` tool vs a separate artifact — interacts with `package-publishing`'s "tool carries no core payload" requirement and is not pinned here (Open Question A).

### D4 — Deletion surface (staged)
Delete: `Dotnet.Script.Core` usage + the `.csx` loader; the `config.yaml` extension loader + the user/project union + per-entry fail-soft; the `AssemblyLoadContext` reflection discovery + `AssemblyDependencyResolver` probing; the runtime NuGet downloader; the `promote` path; the `extension.load`/`extension.unload`/`extension.list` RPC commands and the `ExtensionUnloadedEvent`. Retain: the `IDmonExtension`/`AIFunction` contract, `IDmonMiddleware`/`DmonMiddlewareAttribute`, the permission pipeline, and the protocol handshake gate.

### D5 — Acquisition is SDK restore; protocol pin survives
No dmon-owned downloader. `#:package dmoncore@<ProtocolVersion.Current Major.Minor>.*` is restored by the SDK; the `agentReady` `protocolVersion` gate (ADR-011 D6) still fires against the resolved/compiled core.

## Risks / Trade-offs

- **stdout contamination breaks the handshake** → build-then-`--no-build`-run (D2); an end-to-end test must assert the child's first stdout line is `agentReady`/a frame, never MSBuild output. Residual: confirm `dotnet run --no-build` is silent for the *file-based-program* path; fall back to exec-the-built-dll if not (ADR-019 OQ-C).
- **SDK now required for the `Dmon.cs` path** → the prebuilt default preserves the runtime-only experience; an actionable error fires only when a user-authored `Dmon.cs` is present but unbuildable (ADR-019 OQ-B).
- **Large breaking deletion** → existing `.csx`/config-declared extensions stop working; migration is "rewrite as a `#:package` + builder call in `Dmon.cs`." No production deployments, so a clean break is acceptable (project convention).
- **RPC command retirement may touch `protocol-schema`** → `extension.load`/`unload`/`list` are retired; if they are modelled in the `protocol-schema` spec, that needs a follow-up delta. Flagged, not folded in here.
- **Sequencing hazard** → once `Dmon.cs` is the composition root, an agent that can edit files can compile code into its own core. This change ships the mechanism but **not** the gate; the `compose` tier (ADR-021) must land before the agent is permitted to self-modify composition. Until then, `Dmon.cs` edits are a human/operator action.

## Migration Plan

No data migration (no production deployments). Rollout: ship the `dmoncore` library + prebuilt default; `Dmon.Runtime` gains the new precedence + build-then-run; delete the dynamic-load tier. Existing users with `config.yaml` extension lists migrate by running `dmon init` and moving each entry to a `#:package` + builder call. Rollback is reverting the build; the prebuilt default is inert to older hosts.

## Open Questions

- **A. Prebuilt-default distribution.** How the no-SDK default core is delivered (bundled in the `dmon` tool vs a separate fetched artifact), reconciling with `package-publishing`'s "tool carries no core payload." (ADR-019 D9 / OQ-B.)
- **B. `dotnet run --no-build` stdout silence for file-based programs.** Confirm at implementation; `--verbosity quiet` or exec-the-built-dll as fallback (ADR-019 OQ-C).
- **C. `protocol-schema` impact.** Whether retiring the `extension.load`/`unload`/`list` commands requires a delta to the `protocol-schema` spec (follow-up change).
- **D. SDK detection mechanics** for the present-but-unbuildable-`Dmon.cs` error path (ADR-019 OQ-B).
