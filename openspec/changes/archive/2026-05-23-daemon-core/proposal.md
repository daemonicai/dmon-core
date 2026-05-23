## Why

There is no .NET-native coding agent with the ergonomics of Pi — self-extensible, multi-provider, session-as-data. daemon fills that gap: a C# / .NET 10 agent built natively for the .NET ecosystem, not ported from TypeScript.

## What Changes

This is a greenfield project. V1 delivers the agent core and a console/TUI host.

- **New**: Agent core process exposing a Pi-compatible JSONL-over-stdio RPC surface
- **New**: Provider registry with runtime switching — Claude, OpenAI, Gemini, Ollama, llama.cpp, oMLX (six providers via three `IChatClient` adapters: `openai`, `anthropic`, `gemini` — local providers reuse the OpenAI-compat adapter)
- **New**: Two-tier extension model: `.csx` scripts (Dotnet.Script.Core, NuGet-aware) and NuGet packages (`IDaemonExtension` / `AIFunction`)
- **New**: `promote` command — graduates a working `.csx` script into a scaffolded NuGet extension
- **New**: Session storage — JSONL messages, project-local by default, non-destructive compaction
- **New**: Conservative permission model — Read/Write/Edit/Delete/Bash/HTTP gate with tree-based path grants, composite-bash always-prompt, hardcoded denylist
- **New**: API key auth with `/login` and `/logout` commands; credentials always user-global
- **New**: Console/TUI host (thin RPC frontend)
- **New**: OpenTelemetry instrumentation in `Daemon.Core` — traces, metrics, logs — configured via standard `OTEL_*` environment variables

Out of scope for V1: Avalonia desktop host, multi-agent orchestration, skill marketplace.

## Capabilities

### New Capabilities

- `agent-core`: The agent process — RPC surface, session lifecycle, turn execution loop, compaction
- `provider-registry`: `IChatClient`-based provider abstraction, config-driven registry, runtime switching, capability negotiation
- `extension-model`: `.csx` script loading (Dotnet.Script.Core), NuGet extension loading (`AssemblyLoadContext`), `IDaemonExtension` contract, tool registry, `promote` command
- `session-storage`: JSONL session files, `meta.json`, attachments, project-local discovery, SQLite global index, fork/clone/compaction
- `permission-model`: Read/Write/Edit/Delete/Bash/HTTP permission gate, tree grants, glob pattern approval, composite-bash policy, denylist, settings persistence
- `auth`: API key credential resolution (`IConfiguration` → env var → credentials file → prompt), `/login` and `/logout` commands
- `console-host`: Console/TUI frontend over the RPC surface

### Modified Capabilities

*(none — greenfield)*

## Impact

- **New solution**: `Daemon.sln` with projects for protocol contracts, core, console host, extensions SDK, and tests. `Daemon.Console` references only `Daemon.Protocol` (process boundary preserved).
- **New dependencies**: `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `Anthropic.SDK`, `GeminiDotnet.Extensions.AI`, `Dotnet.Script.Core`, `Microsoft.Data.Sqlite`, `NetEscapades.Configuration.Yaml`, `Spectre.Console`
- **New public package**: `Daemon.Extensions` is intended to be NuGet-published so third parties can author extensions without a daemon source checkout
- **New config**: `.daemon/config.yaml` (project-local), `~/.daemon/config.yaml` (user-global)
- **New credentials store**: `~/.daemon/credentials/<provider>.json`
- **ADRs**: Six architecture decision records in `docs/adrs/` cover all major decisions
