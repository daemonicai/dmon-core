## Context

daemon is a greenfield .NET 10 / C# coding agent inspired by Pi. There is no existing codebase. Six ADRs (in `docs/adrs/`) capture the major architectural decisions made during the exploration phase; this document summarises them and adds cross-cutting design details.

The agent runs as a separate process and exposes a JSONL-over-stdio RPC surface. Host frontends (console/TUI for V1, Avalonia for V1.5+) are thin clients over that surface. This separation is load-bearing: it enables third-party frontends, remote operation, and multi-host without re-architecting.

## Goals / Non-Goals

**Goals:**
- Agent core process with full turn execution loop, session lifecycle, and provider switching
- Conservative permission model that requires explicit user approval for write/execute operations
- Two-tier extension model supporting runtime self-extension by the agent
- Console/TUI host for everyday use
- Pi-compatible RPC surface (existing Pi frontends should work with a thin adapter)

**Non-Goals:**
- Avalonia desktop host (V1.5+)
- Multi-agent orchestration
- OAuth authentication (API keys only for V1)
- AOT-trimmed distribution profile (defer until the JIT profile is proven)
- Skill marketplace or extension discovery service

## Decisions

### D1: IChatClient as the provider abstraction (ADR-001)
`Microsoft.Extensions.AI.IChatClient` over Microsoft Agent Framework. All six target providers (Claude, OpenAI, Gemini, oMLX, Ollama, llama.cpp) have `IChatClient` implementations available as NuGet packages. A config-driven registry maps provider names to `IChatClient` factories; the agent loop holds a swappable reference. MAF's value is multi-agent orchestration, which is out of scope.

### D2: AIFunction / IDaemonExtension extension contract (ADR-002)
Extensions expose `IEnumerable<AIFunction>` via a minimal `IDaemonExtension` interface. Extensions are pure `Microsoft.Extensions.AI` code; authors need know nothing of daemon internals. `.csx` scripts are loaded via `Dotnet.Script.Core` (not raw Roslyn) to support `#r "nuget:..."` package resolution at runtime. NuGet extensions are loaded via collectible `AssemblyLoadContext`. The `promote` command scaffolds a `.csx` script into a NuGet extension, extracting `#r` directives into `<PackageReference>` elements automatically.

**Spike required**: `Dotnet.Script.Core`'s embedding API must be validated before implementation. If the spike fails, fallback is raw Roslyn scripting (no NuGet resolution in scripts).

### D3: Pi-style JSONL RPC protocol (ADR-003)
JSONL-over-stdio, not strict JSON-RPC 2.0. Commands are fire-and-forget; events flow back as newline-delimited JSON objects. Tool confirmation uses Pi's `extension_ui_request` pattern: the core emits `tool.confirmRequest {id, name, args, risk}` and suspends until the host responds with `tool.confirmResponse`. `risk: none|low|high` lets the host decide presentation. camelCase throughout (Pi uses snake_case).

### D4: JSONL session storage (ADR-004)
Sessions are relocatable directories: `messages.jsonl` (append-only), `meta.json`, `attachments/` (large tool outputs over a configurable threshold, default 1 KiB). Compaction appends a `CompactionMessage` marker — non-destructive, full history preserved. A SQLite index (`sessions.db`) enables fast session listing; it is a cache, not a source of truth. Sessions are project-local by default (walk up from CWD for `.daemon/`), redirectable to user-global via config.

### D5: Conservative permission model (ADR-006)
Read within CWD subtree: implicit. Everything else: prompted. Tree-based grants (allow `/path` → allow `/path/**`). Bash: simple commands use glob-pattern approval; composites (pipes, `;`, `&&`, `$()`, redirects) always prompt regardless of stored approvals. HTTP: per-domain, project-scoped only. A hardcoded denylist of dangerous patterns cannot be overridden. Permissions persist at session, project (`.daemon/settings.yaml`), or global (`~/.daemon/settings.yaml`) scope.

### D6: API key auth (ADR-005)
All V1 providers use API keys. Credential resolution order: environment variable → `~/.daemon/credentials/<provider>.json` → interactive prompt. Credentials are always user-global. `/login` and `/logout` are UI commands that map to `auth.login` / `auth.logout` RPC messages.

### D7: Solution structure
```
Daemon.sln
  src/
    Daemon.Protocol/      — wire DTOs/enums for the JSONL RPC surface (no logic)
    Daemon.Core/          — agent loop, session, RPC surface, permission gate
    Daemon.Extensions/    — IDaemonExtension, AIFunction helpers (NuGet-publishable)
    Daemon.Console/       — console/TUI host
  test/
    Daemon.Core.Tests/
    Daemon.Extensions.Tests/
    Daemon.Protocol.Tests/
  spike/
    ScriptingSpike/       — Dotnet.Script.Core embedding validation
```

**`Daemon.Protocol` is the only project shared between Core and Console.** It contains the record types, enums, and constants for every command and event in ADR-003 — no logic, no provider SDKs, no I/O. This preserves the process boundary (Console cannot call Core internals because it doesn't reference Core) while giving both sides compile-time safety against protocol drift. Third-party .NET frontends can NuGet-reference `Daemon.Protocol` to talk to the core without pulling in any agent machinery.

`Daemon.Console` references **only** `Daemon.Protocol` (plus `Spectre.Console` and the BCL). It does not reference `Daemon.Core` or `Daemon.Extensions`.

### D8: Permission gate placement
The permission gate is `IChatClient` middleware, inserted before `FunctionInvokingChatClient` in the pipeline:

```
IChatClient pipeline:
  .UsePermissionGate(policyProvider)   ← intercepts tool calls, enforces policy
  .UseFunctionInvocation()             ← M.E.AI dispatch loop
  .Build()
```

All tools — built-in and extension — pass through the same gate. No special cases.

## Cross-cutting References

Authoritative locations for cross-cutting concerns an implementer will need:

| Topic | Source of truth |
|-------|----------------|
| RPC commands, events, payload shapes | ADR-003 (full tables) |
| `messageDelta` delta types | ADR-003 |
| Single-instance enforcement (session lock) | ADR-003 |
| Transient-error retry policy | ADR-003 |
| Credentials file schema, file permissions | ADR-005 |
| `ui.inputRequest` vs `tool.confirmRequest` | ADR-003 + ADR-005 |
| Bash composite grammar | ADR-006 |
| Hardcoded denylist contents | ADR-006 |
| Grant precedence rules | ADR-006 |
| Mid-turn `model.set` semantics | ADR-001 |
| `extension.load` permission gating | ADR-002 |
| Fork mechanics (copy + truncate-copy) | ADR-004 |

## Risks / Trade-offs

- **Dotnet.Script.Core embedding is unverified** → Spike before implementation. Fallback: raw Roslyn (no NuGet resolution in scripts).
- **Community dependency on Dotnet.Script.Core** → Actively maintained, .NET 10 compatible (May 2026). Fallback path exists.
- **Pi protocol compatibility** → Not byte-for-byte compatible (camelCase vs snake_case, bash not a top-level command). A Pi-adapter is out of scope for V1; if built later it lives outside the core.
- **Composite bash always-prompt** → Higher friction for power users who pipe commands frequently. Considered acceptable for a conservative-by-design tool.
- **JSONL scanning for fork** → Linear scan to find fork point. Fast enough for pre-compaction sessions; no index added until profiling shows otherwise.
- **Anthropic OAuth unavailable** → Anthropic disabled third-party OAuth Feb 2026. API key only. If re-enabled, device code flow path designed for Gemini applies.

### D9: Console host uses Spectre.Console
Spectre.Console is the TUI library. Rationale: V1 UI is a streaming chat with slash-commands and confirmation prompts — not a full-screen panelled UI. Spectre's prompts, live displays, and markup are a direct fit; Terminal.Gui's window manager is overkill.

### D10: `.daemon/` is created on first use
No `daemon init` command in V1. The first session in a project that has no `.daemon/` in its ancestor tree creates `.daemon/` at CWD with a default `config.yaml` and an empty `sessions/` directory. The host announces this via a one-line `bootstrapNotice` event so the user knows files were written.

### D11: Thinking level is a daemon-level abstraction
`thinking.set {level}` takes one of `off | low | medium | high`. Each provider adapter maps these to its native parameter (Anthropic `thinking.budget_tokens`, OpenAI `reasoning_effort`, Gemini `thinkingBudget`). `thinking.cycle` advances through the four levels in order. Providers that don't support reasoning ignore the value and emit a `capabilityIgnored` event.

### D12: `Daemon.Core` is built on the `dotnet new worker` template
The core process uses the Worker Service template as its skeleton: it provides `Host.CreateApplicationBuilder` with `IConfiguration` (env vars, JSON, command-line — extended with YAML for our config), `ILogger`/`ILoggerFactory`, the DI container, lifetime management, and a `BackgroundService` base class. The default `Worker.cs` is replaced with `RpcHostedService : BackgroundService` which owns the JSONL-over-stdio loop. All core components — provider registry, session store, permission gate, tool registry, turn loop — are registered as DI services via extension methods in `Program.cs`. This standardises configuration loading, logging routing (logs go to stderr so they don't collide with the stdio RPC channel on stdout), and graceful shutdown on SIGTERM/Ctrl+C.

### D13: OpenTelemetry instrumentation
`Daemon.Core` is instrumented with OpenTelemetry — traces, metrics, and logs — configured via the standard OTel environment variables. Configuration is intentionally not surfaced in `config.yaml`; we defer entirely to the spec so daemon behaves identically to every other OTel-instrumented process.

**Env vars honoured** (full list at the [OTel spec](https://opentelemetry.io/docs/specs/otel/configuration/sdk-environment-variables/)):

- `OTEL_SDK_DISABLED` — global kill switch (default: SDK active but exporters are no-op without an endpoint)
- `OTEL_SERVICE_NAME` — defaults to `daemon-core`
- `OTEL_RESOURCE_ATTRIBUTES` — appended to defaults (`service.version`, `process.pid`, `host.name`)
- `OTEL_EXPORTER_OTLP_ENDPOINT` / `_HEADERS` / `_PROTOCOL` (and signal-specific overrides)
- `OTEL_TRACES_EXPORTER`, `OTEL_METRICS_EXPORTER`, `OTEL_LOGS_EXPORTER` — `otlp` | `console` | `none`. Default is `none` when no OTLP endpoint is configured, so a daemon without OTel setup pays no exporter overhead.
- `OTEL_TRACES_SAMPLER`, `OTEL_PROPAGATORS`, standard SDK knobs

**What is instrumented:**

| Signal | Surface |
|--------|---------|
| Trace span: `turn` | One per turn. Attributes: `daemon.session.id`, `daemon.provider`, `daemon.model`, `daemon.thinking.level`, `daemon.tokens.input`, `daemon.tokens.output`, `daemon.tokens.cache_read`, `daemon.tokens.cache_write`, `daemon.cost.usd`, `daemon.stop_reason`. |
| Trace span: `provider.call` (child of `turn`) | Per LLM call. Attributes: `daemon.provider`, `daemon.model`, `daemon.retry.attempt`, `gen_ai.*` (where applicable per OTel GenAI semantic conventions). |
| Trace span: `tool.execute` (child of `turn`) | Per tool invocation. Attributes: `daemon.tool.name`, `daemon.tool.args.size_bytes`, `daemon.tool.result.size_bytes`, `daemon.tool.is_error`, `daemon.permission.risk`, `daemon.permission.decision` (`allowOnce`/`allowProject`/`allowGlobal`/`deny`/`implicit`/`denylist`). |
| Trace span: `permission.evaluate` | Per gate evaluation. |
| Trace span: `session.<op>` | `create`, `fork`, `clone`, `compact`. |
| Metric: `daemon.turns` (counter) | Tagged by `provider`, `model`, `stop_reason`. |
| Metric: `daemon.tokens` (counter) | Tagged by `provider`, `model`, `direction` (`input`/`output`/`cache_read`/`cache_write`). |
| Metric: `daemon.cost.usd` (counter) | Tagged by `provider`, `model`. |
| Metric: `daemon.turn.duration` (histogram, ms) | Tagged by `provider`, `model`, `stop_reason`. |
| Metric: `daemon.tool.invocations` (counter) | Tagged by `tool`, `is_error`. |
| Metric: `daemon.permission.prompts` (counter) | Tagged by `risk`, `decision`. |
| Metric: `daemon.provider.retries` (counter) | Tagged by `provider`, `reason`. |
| Logs | `ILogger` output is enriched with the active span context and exported via the OTel Logs SDK to the same endpoint when a logs exporter is configured. |

`HttpClient` calls from provider SDKs are auto-instrumented via `OpenTelemetry.Instrumentation.Http` so request-level details on provider calls are captured without per-SDK wiring.

**No PII in span attributes.** Message content, tool arguments, and tool results are never attached to spans or metrics — only sizes, counts, and identifiers. A future opt-in flag (`Daemon:Telemetry:CapturePromptContent`) can expose this for self-hosted debugging, but it is off by default and never honoured if the OTLP endpoint is non-localhost.

## Open Questions

*(none — resolved above)*
