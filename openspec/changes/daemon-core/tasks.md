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

## 6. Permission Model

- [x] 6.1 Implement `IPermissionPolicy` — CWD subtree read, tree-based path grants, bash glob matching, composite detection, HTTP domain, denylist
- [x] 6.2 Implement permission settings persistence — read/write `.daemon/settings.yaml` and `~/.daemon/settings.yaml`
- [x] 6.3 Implement bash composite detection per ADR-006: pipes (`|`, `|&`), separators (`;`, `&&`, `||`, newlines, `&`), substitution (`$()`, backticks, `<()`, `>()`), redirects (`>`, `>>`, `<`, `<<`, `<<<`, `2>`, `&>`, `>&`), subshells/groups, inline env assignments. Ambiguous parses fail safe to composite.
- [x] 6.4 Implement hardcoded denylist per ADR-006 (`rm` against root/system dirs, `mkfs`/`dd if=/dev/zero`/`shred`, `chmod -R 777 /`/`chattr -i /`, fork bombs, `sudo`/`su`) with test coverage for all pattern categories
- [x] 6.4.1 Implement grant-precedence resolution: most-specific path prefix wins; bash deny beats allow; project beats global
- [x] 6.5 Implement `PermissionGateChatClient` middleware — intercepts tool calls, evaluates policy, emits `tool.confirmRequest`, suspends until `tool.confirmResponse`
- [x] 6.6 Wire `PermissionGateChatClient` before `FunctionInvokingChatClient` in the `IChatClient` pipeline
- [x] 6.7 Write unit tests for permission policy, composite detection, denylist, and grant persistence

## 7. Auth

- [x] 7.1 Implement credential resolver — env var → `~/.daemon/credentials/<provider>.json` → interactive prompt via `ui.inputRequest {kind: "secret"}`
- [x] 7.1.1 Implement credentials file read/write with documented schema (provider, type, apiKey, headerStyle, createdAt, updatedAt); enforce mode `0600` on POSIX, restricted ACL on Windows; create parent dir with `0700`
- [x] 7.2 Implement `auth.login` / `auth.logout` RPC command handlers using `ui.inputRequest`/`ui.inputResponse` for secret entry
- [x] 7.3 Implement `auth.status` response with per-provider authentication state
- [x] 7.4 Wire credential resolver into provider registry factory
- [x] 7.5 Write unit tests for credential resolution order

## 8. Extension Model

- [x] 8.1 Implement NuGet extension loader — `AssemblyLoadContext`, `IDaemonExtension` discovery by reflection
- [x] 8.2 Implement `.csx` script loader using `Dotnet.Script.Core` (or raw Roslyn if spike fails — see task 2.5)
- [x] 8.3 Implement `IToolRegistry` — per-session registry, `ChatOptions.Tools` built per-call
- [x] 8.4 Implement `extension.load`, `extension.unload` RPC command handlers; `extension.load` MUST surface to the permission gate (`risk: high`) before any network call or assembly load. Resolved NuGet packages cache to `~/.daemon/extensions/<package>/<version>/`. Failures emit `extensionError {source, phase, diagnostics[]}` with no partial registration.
- [x] 8.5 Implement `extension.promote` — scaffold `IDaemonExtension` class + `.csproj`, extract `#r` directives to `<PackageReference>` elements
- [x] 8.6 Write unit tests for NuGet loader, script loader, tool registry, and promote scaffolding

## 9. Agent Core — RPC Surface and Turn Loop

- [x] 9.0 Replace the default `Worker.cs` from the worker template with a `RpcHostedService : BackgroundService` that owns the stdio reader/writer loop. Wire core components (provider registry, session store, permission gate, tool registry, turn loop) into the host's DI container via `Program.cs` extension methods (`AddDaemonCore`, `AddProviderRegistry`, etc.).
- [x] 9.1 Implement JSONL-over-stdio reader/writer (LF-delimited, strip trailing CR) inside `RpcHostedService`
- [x] 9.2 Implement command dispatcher — route incoming commands to handlers
- [x] 9.3 Implement event emitter — write events to stdout (full event/payload catalogue in ADR-003)
- [x] 9.3.1 Emit `agentReady {protocolVersion, coreVersion}` on startup before processing any command
- [ ] 9.3.2 Emit `bootstrapNotice {path, created[]}` when `.daemon/` is auto-created on first use
- [x] 9.4 Implement turn execution loop — `turn.submit`, `turn.steer`, `turn.followUp`, `turn.abort`
- [x] 9.5 Implement `turnStart` / `messageDelta` / `toolExecutionStart` / `toolExecutionEnd` / `turnEnd` event emission (payloads per ADR-003)
- [x] 9.5.1 Implement `ui.inputRequest`/`ui.inputResponse` channel for secret/text/select input (distinct from `tool.confirmRequest`)
- [ ] 9.6 Implement session command handlers — `session.create`, `session.fork`, `session.clone`, `session.load`, `session.list`, `session.setName`, `session.getStats`, `session.getMessages`
- [ ] 9.6.1 Implement session directory advisory lock (`<id>/.lock` via `flock`/`LockFileEx`); second core attempting the same session emits `error {code: "sessionLocked"}` and exits non-zero
- [ ] 9.7 Implement thinking level abstraction — `thinking.set {level: off|low|medium|high}` and `thinking.cycle`; each provider adapter maps the level to its native reasoning parameter; emit `capabilityIgnored` when active model lacks reasoning support
- [ ] 9.8 Implement transient-error retry per ADR-003: exponential backoff with jitter (`baseDelay`, `maxDelay`, `maxAttempts` from `IConfiguration` under `Daemon:Provider:Retry:*`); honour `Retry-After`; emit `retryAttempt` per attempt; non-retryable errors end the turn with `stopReason: error`
- [ ] 9.9 Write integration tests for turn loop with a mock `IChatClient`

## 10. Console Host

- [ ] 10.1 Integrate Spectre.Console (decision D9): streaming display for `messageDelta`, prompts for `tool.confirmRequest`/`ui.inputRequest`, markup for risk-level visual differentiation
- [ ] 10.2 Implement core process spawner — launch `Daemon.Core` and connect stdio pipes
- [ ] 10.3 Implement event renderer — display `messageDelta` streaming text, `toolExecutionStart/End`
- [ ] 10.4 Implement user input prompt and slash command parser
- [ ] 10.5 Implement `tool.confirmRequest` UI — display name, args, risk level (high-risk visually distinct); offer four options (Allow once / Allow for project / Allow globally / Deny); for composite bash, offer only Allow once / Deny
- [ ] 10.5.1 Implement `ui.inputRequest` UI — text/secret/select kinds; secret input is masked; bootstrap-notice rendered as one-line info
- [ ] 10.6 Implement session management slash commands (`/new`, `/fork`, `/clone`)
- [ ] 10.7 Implement provider switching slash commands (`/model`, `/model <provider> <id>`)
- [ ] 10.8 Implement auth slash commands (`/login <provider>`, `/logout <provider>`)
- [ ] 10.9 Implement extension slash commands (`/load`, `/unload`, `/promote`)
- [ ] 10.9.1 Implement `/thinking [level]` — set or cycle thinking level
- [ ] 10.10 Write end-to-end smoke test — launch core, submit a turn, verify response rendered

## 10b. OpenTelemetry Instrumentation

- [ ] 10b.1 Wire OTel into the worker host: `services.AddOpenTelemetry().WithTracing(...).WithMetrics(...).WithLogging(...)`. Service name defaults to `daemon-core`; resource attributes include `service.version`, `process.pid`, `host.name`. SDK reads standard env vars (`OTEL_*`) — no daemon-specific config keys.
- [ ] 10b.2 Default exporters to `none` when no OTLP endpoint is configured so an unconfigured daemon pays no exporter cost.
- [ ] 10b.3 Define an `ActivitySource` (`Daemon.Core`) and a `Meter` (`Daemon.Core`). Emit spans per design.md D13 table: `turn`, `provider.call`, `tool.execute`, `permission.evaluate`, `session.<op>`.
- [ ] 10b.4 Emit metrics per design.md D13 table: `daemon.turns`, `daemon.tokens`, `daemon.cost.usd`, `daemon.turn.duration`, `daemon.tool.invocations`, `daemon.permission.prompts`, `daemon.provider.retries`.
- [ ] 10b.5 Add `OpenTelemetry.Instrumentation.Http` so provider HTTP calls are auto-traced; verify no double-spanning with the manual `provider.call` span.
- [ ] 10b.6 Route `ILogger` output through the OTel Logs pipeline and ensure structured-log fields and active span context are attached.
- [ ] 10b.7 Enforce the no-PII rule: assert in unit tests that no span attribute or metric tag carries message content, tool arguments, or tool results — only sizes, counts, and identifiers. The opt-in `Daemon:Telemetry:CapturePromptContent` flag is out of scope for V1 — do not wire it.
- [ ] 10b.8 Document the supported `OTEL_*` env vars and the daemon-specific span/metric names in `docs/observability.md`.

## 11. Integration and Polish

- [ ] 11.1 Wire all components together in `Daemon.Core` startup — registry, session store, permission gate, tool registry, turn loop
- [ ] 11.2 Implement `.daemon/` initialisation on first use (decision D10): create directory + default `config.yaml` + empty `sessions/`, emit `bootstrapNotice`
- [ ] 11.3 Add `IConfiguration`-based settings loading with documented keys (YAML provider; document `Daemon:Session:AttachmentThresholdBytes`, `Daemon:Provider:Retry:*`, `Daemon:SessionStore`, `Daemon:Compaction:Threshold`)
- [ ] 11.4 Write end-to-end integration test — real `IChatClient` (stubbed), full turn with tool call and permission confirmation
- [ ] 11.5 *(removed — Pi byte-for-byte compatibility is explicitly not a goal per design.md D3. A Pi-adapter is out of scope for V1; track separately if needed.)*