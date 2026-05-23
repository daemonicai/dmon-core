# Observability

Daemon is instrumented with OpenTelemetry for distributed tracing, metrics, and structured logging. Configuration follows the [OpenTelemetry SDK environment variable specification](https://opentelemetry.io/docs/specs/otel/configuration/sdk-environment-variables/). No daemon-specific YAML configuration is required — standard `OTEL_*` environment variables are used.

## Quick start

To export telemetry to an OTLP collector:

```bash
export OTEL_SERVICE_NAME="my-daemon"
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4318"
daemon-core
```

Without an OTLP endpoint, the SDK initialises but all exporters are no-ops — an unconfigured daemon pays no exporter overhead.

## Environment variables

### Service identification

| Variable | Default | Description |
|----------|---------|-------------|
| `OTEL_SERVICE_NAME` | `daemon-core` | Logical service name. |
| `OTEL_RESOURCE_ATTRIBUTES` | — | Appended to default resource attributes (`service.version`, `process.pid`, `host.name`). Format: `key1=val1,key2=val2`. |

### Global SDK

| Variable | Default | Description |
|----------|---------|-------------|
| `OTEL_SDK_DISABLED` | `false` | Global kill switch. When `true`, no telemetry is collected. |

### Exporters

| Variable | Default | Description |
|----------|---------|-------------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — | Base URL for OTLP exporter (e.g. `http://localhost:4318`). |
| `OTEL_EXPORTER_OTLP_HEADERS` | — | Key-value pairs for OTLP request headers. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` | Transport protocol (`http/protobuf` or `grpc`). |

### Per-signal exporters

Each of these accepts `otlp`, `console`, or `none`. When no OTLP endpoint is configured, defaults are effectively `none`.

| Variable | Description |
|----------|-------------|
| `OTEL_TRACES_EXPORTER` | Exporter for trace spans. |
| `OTEL_METRICS_EXPORTER` | Exporter for metrics. |
| `OTEL_LOGS_EXPORTER` | Exporter for logs. |

### Tracing

| Variable | Default | Description |
|----------|---------|-------------|
| `OTEL_TRACES_SAMPLER` | `always_on` | Sampling strategy (`always_on`, `always_off`, `traceidratio`, `parentbased_always_on`, etc.). |
| `OTEL_TRACES_SAMPLER_ARG` | — | Argument for the sampler (e.g. ratio for `traceidratio`). |

### Propagation

| Variable | Default | Description |
|----------|---------|-------------|
| `OTEL_PROPAGATORS` | `tracecontext,baggage` | Comma-separated list of context propagators. |

## Spans

All spans are created under the `Daemon.Core` activity source.

### `turn`

One per agent turn. Root span for the entire turn execution.

| Attribute | Type | Description |
|-----------|------|-------------|
| `daemon.session.id` | string | Session identifier. |
| `daemon.provider` | string | LLM provider name (e.g. `anthropic`, `openai`). |
| `daemon.model` | string | Model identifier. |
| `daemon.thinking.level` | string | Thinking level (`off`, `low`, `medium`, `high`). |
| `daemon.tokens.input` | int64 | Input token count for the turn. |
| `daemon.tokens.output` | int64 | Output token count for the turn. |
| `daemon.tokens.cache_read` | int64 | Cache read tokens (Anthropic prompt caching). |
| `daemon.tokens.cache_write` | int64 | Cache write tokens (Anthropic prompt caching). |
| `daemon.cost.usd` | double | Estimated cost in USD. |
| `daemon.stop_reason` | string | Turn stop reason (e.g. `completed`, `cancelled`, `error`). |

### `provider.call`

Child of `turn`. One per LLM API call (including retries).

| Attribute | Type | Description |
|-----------|------|-------------|
| `daemon.provider` | string | Provider name. |
| `daemon.model` | string | Model identifier. |
| `daemon.retry.attempt` | int64 | 0-indexed attempt number. |

### `tool.execute`

Child of `turn`. One per tool invocation.

| Attribute | Type | Description |
|-----------|------|-------------|
| `daemon.tool.name` | string | Tool name. |
| `daemon.tool.args.size_bytes` | int64 | Size of tool arguments in bytes. |
| `daemon.tool.result.size_bytes` | int64 | Size of tool result in bytes. |
| `daemon.tool.is_error` | bool | Whether the tool execution errored. |
| `daemon.permission.risk` | string | Risk level at time of permission check. |
| `daemon.permission.decision` | string | Permission decision (`allowonce`, `allowproject`, `allowglobal`, `deny`, `implicit`, `denylist`). |

### `permission.evaluate`

Child of `turn`. One per tool call permission evaluation.

| Attribute | Type | Description |
|-----------|------|-------------|
| `daemon.tool.name` | string | Tool name being evaluated. |
| `daemon.permission.risk` | string | Assessed risk level. |
| `daemon.permission.decision` | string | Final decision. |

### `session.<op>`

One per session lifecycle operation.

| Span name | Operation |
|-----------|-----------|
| `session.create` | New session created. |
| `session.fork` | Session forked at a checkpoint. |
| `session.clone` | Session cloned. |
| `session.compact` | Compaction marker appended. |

## Metrics

All metrics are emitted under the `Daemon.Core` meter. All counters are monotonic.

### Counters

| Metric | Type | Tags | Description |
|--------|------|------|-------------|
| `daemon.turns` | Counter\<int64\> | `provider`, `model`, `stop_reason` | Number of completed turns. |
| `daemon.tokens` | Counter\<int64\> | `provider`, `model`, `direction` | Token usage (`input`, `output`). |
| `daemon.cost.usd` | Counter\<double\> | `provider`, `model` | Estimated cost in USD. |
| `daemon.tool.invocations` | Counter\<int64\> | `tool`, `is_error` | Tool invocation count. |
| `daemon.permission.prompts` | Counter\<int64\> | `risk`, `decision` | Permission prompt count. |
| `daemon.provider.retries` | Counter\<int64\> | `provider`, `reason` | Provider retry count. |

### Histograms

| Metric | Type | Unit | Tags | Description |
|--------|------|------|------|-------------|
| `daemon.turn.duration` | Histogram\<double\> | ms | `provider`, `model`, `stop_reason` | Turn wall-clock duration. |

## Logs

`ILogger` output is exported through the OpenTelemetry Logs SDK. Active span context and structured-log fields are attached to each log record.

Console logging to stderr is preserved alongside OTLP log export — both pipelines operate simultaneously.

## Privacy

**No PII in span attributes or metric tags.** Message content, tool arguments, and tool results are never attached to telemetry — only sizes (bytes, counts) and non-sensitive identifiers (provider names, model IDs, tool names, decisions).

A future opt-in flag `Daemon:Telemetry:CapturePromptContent` may expose prompt/response content for self-hosted debugging, but it is not implemented in V1 and must never be honoured when the OTLP endpoint is non-localhost.