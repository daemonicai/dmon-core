## 1. Solution Scaffolding

- [x] 1.1 Create `Daemon.sln` with project structure: `src/Daemon.Protocol` (`dotnet new classlib`), `src/Daemon.Core` (`dotnet new worker` — provides `Host`, `IConfiguration`, `ILogger`, DI container, and a `BackgroundService` lifecycle suitable for the long-running agent process), `src/Daemon.Extensions` (`dotnet new classlib`), `src/Daemon.Console` (`dotnet new console`), `test/Daemon.Protocol.Tests`, `test/Daemon.Core.Tests`, `test/Daemon.Extensions.Tests`, `spike/ScriptingSpike`
- [x] 1.2 Populate `Daemon.Protocol` from ADR-003: record types and enums for every host→core command, core→host event, the `messageDelta` delta types, the `Model` capability object, `risk` levels, and `stopReason` values. No logic, no I/O, no dependencies beyond the BCL and `System.Text.Json` source generator attributes. Mark public; this project is NuGet-publishable so third-party .NET frontends can consume it.
- [x] 1.3 Add NuGet dependencies to `Daemon.Core`: `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `Anthropic.SDK`, `GeminiDotnet.Extensions.AI`, `Microsoft.Data.Sqlite`, `Microsoft.Extensions.Configuration`, `NetEscapades.Configuration.Yaml`, `Dotnet.Script.Core` (pending spike — see task 2.5), `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime`; project reference to `Daemon.Protocol`.
- [x] 1.4 Add NuGet dependencies to `Daemon.Extensions`: `Microsoft.Extensions.AI`. No reference to `Daemon.Core` or `Daemon.Protocol` — extension authors write pure M.E.AI code.
- [x] 1.5 Add NuGet dependencies to `Daemon.Console`: `Spectre.Console`; project reference to `Daemon.Protocol` **only**. Do not reference `Daemon.Core` or `Daemon.Extensions` — the console host talks to the core over stdio as a subprocess.
- [x] 1.6 Create `.daemon/config.yaml` schema and sample config with one provider entry per adapter type

## 2. Spike — Dotnet.Script.Core Embedding

- [x] 2.1 Add `Dotnet.Script.Core` to `spike/ScriptingSpike` and write a host that loads a `.csx` file from disk
- [x] 2.2 Verify `#r "nuget:..."` resolution works inside a hosted script context
- [x] 2.3 Verify the script can return an `AIFunction` instance accessible to the host
- [x] 2.4 Verify the loaded script's assemblies can be isolated in a collectible `AssemblyLoadContext`
- [x] 2.5 Document findings; update ADR-002 if the fallback (raw Roslyn) is needed

## 3. Daemon.Extensions — IDaemonExtension Contract

- [x] 3.1 Define `IDaemonExtension` interface
- [x] 3.2 Add XML doc comments sufficient for NuGet consumers
- [x] 3.3 Add `AIFunctionFactory` helper utilities to the package for common extension patterns
- [x] 3.4 Write unit tests for the contract

## 4. Provider Registry

- [x] 4.1 Define `ProviderConfig` model (adapter type, baseUrl, model, auth)
- [x] 4.2 Implement `IProviderRegistry` with `IChatClient` factory resolution for `openai`, `anthropic`, `gemini` adapters
- [x] 4.3 Implement config loading from `.daemon/config.yaml` and `~/.daemon/config.yaml` via `IConfiguration`
- [x] 4.4 Implement `Model` capability metadata object and `model.list` response
- [x] 4.5 Implement runtime provider switching (`model.set`, `model.cycle`)
- [x] 4.6 Implement capability negotiation — agent loop reads `toolCalling` before building `ChatOptions.Tools`
- [x] 4.7 Write unit tests for provider registry and switching

## 5. Session Storage

- [x] 5.1 Implement session directory discovery (walk up from CWD for `.daemon/`, fallback to `~/.daemon/`)
- [x] 5.2 Implement `SessionStore` — create, load, list sessions
- [x] 5.3 Implement `messages.jsonl` append writer
- [x] 5.4 Implement attachment threshold logic using `IConfiguration` (`Daemon:Session:AttachmentThresholdBytes`, default 1024)
- [x] 5.5 Implement `meta.json` read/write
- [x] 5.6 Implement SQLite global index (`sessions.db`) — upsert on create/modify, rebuild-from-scan on corruption
- [x] 5.7 Implement `session.fork` — `cp -r` source directory to new session id, truncate the *new copy's* `messages.jsonl` after the line containing `entryId` (source is never mutated), retain `attachments/` referenced by retained messages, rewrite `meta.json` with new id and `parentSession`/`forkEntryId`
- [x] 5.8 Implement `session.clone` — full directory copy with new id
- [x] 5.9 Implement `CompactionMessage` appending and reader-side compaction marker handling
- [x] 5.10 Write unit tests for session discovery, append, fork, clone, compaction