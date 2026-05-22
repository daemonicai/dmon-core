# daemon — Project Core

## What it is
.NET 10 / C# 13 coding agent. Agent process (`Daemon.Core`) exposes JSONL-over-stdio RPC. Two planned host surfaces: console/TUI and Avalonia desktop (Avalonia is out of scope for V1).

## Solution file
`Daemon.slnx` (XML solution format, NOT `.sln`). Always build with `dotnet build Daemon.slnx`.

## Source map
- `src/Daemon.Protocol` — RPC record types, no logic, no I/O. NuGet-publishable.
- `src/Daemon.Core` — Worker service host. All agent logic lives here.
  - `Providers/` — IChatClient factory, provider registry, config loading
  - `Session/` — Session storage (ADR-004): store, index, appender, attachments, fork/clone, compaction
- `src/Daemon.Extensions` — IDaemonExtension contract + AIFunctionFactory helpers
- `src/Daemon.Console` — Console host. References only Daemon.Protocol; spawns Core as subprocess.
- `spike/ScriptingSpike` — Dotnet.Script.Core embedding spike

## Test projects
- `test/Daemon.Core.Tests` — mirrors `src/Daemon.Core` namespace structure
- `test/Daemon.Extensions.Tests`
- `test/Daemon.Protocol.Tests`

## Key binding ADRs
See `mem:adrs` for binding decisions summary.

## Code conventions
See `mem:conventions`.

## Build / test commands
See `mem:suggested_commands`.
