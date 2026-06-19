# services/

This bucket holds standalone backing server applications that pair with a `tools/` extension package to expose a capability to the dmon agent.

**Services are app artifacts, independently versioned.** They are NOT on the protocol-lockstep train described in ADR-024 (which governs NuGet packages on the `core/`, `providers/`, `tools/`, and `memory/` tracks). Each service is deployed and released on its own schedule.

## Contents

| Directory | Description |
|-----------|-------------|
| `Dcal/`   | iCal-sync calendar server. Backs `tools/Dmon.Tools.Dcal`. Reads `DCAL_ICAL_URL`, syncs into SQLite, and exposes an HTTP lookup surface. |

## Area solution

`services.slnx` at the repository root is the area solution for this bucket. Use it for focused work on services and their tests.

## Adding a new service

1. Create `services/<Name>/` with its own `.csproj` (use `Sdk="Microsoft.NET.Sdk.Web"`, `IsPackable=false`).
2. Create a corresponding test project under `test/<Name>.Tests/`.
3. Add both to `services.slnx` and to `Everything.slnx`.
4. Do NOT add a `services/Directory.Build.props` — the root `Directory.Build.props` applies.
