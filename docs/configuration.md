# daemon — Configuration Reference

daemon reads configuration from two YAML sources (in order, later overrides earlier):

1. `.daemon/config.yaml` — project-local (walked up from CWD)
2. `~/.daemon/config.yaml` — user-global

Both files are optional. When neither exists and `.daemon/` has not been bootstrapped, the core starts with sensible defaults.

---

## Configuration keys

### Top-level keys

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `sessionStore` | string | `"local"` | Where session data is stored. `"local"` = `<project>/.daemon/sessions/`, `"global"` = `~/.daemon/sessions/`, or an absolute path. |
| `providers` | object | — | Provider definitions (see [Provider configuration](#provider-configuration)). |

### Session settings (`Daemon:Session:*`)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Daemon:Session:AttachmentThresholdBytes` | int | `1024` | Tool outputs larger than this (in bytes) are written to `attachments/` rather than inlined in `messages.jsonl`. |
| `Daemon:Session:Compaction:Threshold` | int | `100` | Message count at which auto-compaction should be triggered. |

### Provider retry settings (`Daemon:Provider:Retry:*`)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Daemon:Provider:Retry:BaseDelayMs` | int | `1000` | Initial retry delay in milliseconds for exponential backoff. |
| `Daemon:Provider:Retry:MaxDelayMs` | int | `30000` | Maximum delay cap in milliseconds for exponential backoff. |
| `Daemon:Provider:Retry:MaxAttempts` | int | `5` | Maximum number of retry attempts for transient errors. |

### OpenTelemetry

OTel is configured entirely via standard environment variables. See [observability.md](./observability.md) for the full list of supported `OTEL_*` env vars, span names, and metrics.

---

## Provider configuration

Each provider is defined under the `providers` key in `config.yaml`:

```yaml
providers:
  my-provider:
    adapter: anthropic     # anthropic | openai | gemini
    defaultModelId: claude-sonnet-4-20250514
    baseUrl: ""            # optional, overrides default API base URL
    auth:
      type: envVar         # envVar | file | none
      envVar: ANTHROPIC_API_KEY  # env var name when type is envVar
    capabilities:
      toolCalling: true
      reasoning: true
      contextWindow: 200000
      maxTokens: 4096
```

---

## Example: complete `.daemon/config.yaml`

```yaml
# daemon configuration
sessionStore: local

providers:
  anthropic:
    adapter: anthropic
    defaultModelId: claude-sonnet-4-20250514
    auth:
      type: envVar
      envVar: ANTHROPIC_API_KEY
    capabilities:
      toolCalling: true
      reasoning: true
      contextWindow: 200000
      maxTokens: 4096
  openai:
    adapter: openai
    defaultModelId: gpt-5.2
    auth:
      type: envVar
      envVar: OPENAI_API_KEY
    capabilities:
      toolCalling: true
      reasoning: true
      contextWindow: 128000
      maxTokens: 4096
```
