# dmon — Configuration Reference

dmon reads configuration from two YAML sources (in order, later overrides earlier):

1. `.dmon/config.yaml` — project-local (walked up from CWD)
2. `~/.dmon/config.yaml` — user-global

Both files are optional. When neither exists and `.dmon/` has not been bootstrapped, the core starts with sensible defaults.

---

## Configuration keys

### Top-level keys

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `sessionStore` | string | `"local"` | Where session data is stored. `"local"` = `<project>/.dmon/sessions/`, `"global"` = `~/.dmon/sessions/`, or an absolute path. |
| `providers` | object | — | Provider definitions (see [Provider configuration](#provider-configuration)). |

### Session settings (`Dmon:Session:*`)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Dmon:Session:AttachmentThresholdBytes` | int | `1024` | Tool outputs larger than this (in bytes) are written to `attachments/` rather than inlined in `messages.jsonl`. |
| `Dmon:Session:Compaction:Threshold` | int | `100` | Message count at which auto-compaction should be triggered. |

### Provider retry settings (`Dmon:Provider:Retry:*`)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Dmon:Provider:Retry:BaseDelayMs` | int | `1000` | Initial retry delay in milliseconds for exponential backoff. |
| `Dmon:Provider:Retry:MaxDelayMs` | int | `30000` | Maximum delay cap in milliseconds for exponential backoff. |
| `Dmon:Provider:Retry:MaxAttempts` | int | `5` | Maximum number of retry attempts for transient errors. |

### OpenTelemetry

OTel is configured entirely via standard environment variables. See [observability.md](./observability.md) for the full list of supported `OTEL_*` env vars, span names, and metrics.

---

## Extension loading

NuGet/local-assembly extensions load into the **Default `AssemblyLoadContext`** (`AssemblyLoadContext.Default`). There is no per-extension collectible context. See [ADR-008](./adrs/ADR-008-extension-load-context.md) for the rationale.

**Unload semantics.** `extension.unload <name>` (and the corresponding `ExtensionService.Unload` call) is a **deregister-only** operation: the extension's tools are removed from the registry and are no longer offered to the LLM, but the extension's assembly remains resident in the process. To reclaim the assembly — or to pick up a changed extension — restart the `Dmon.Core` process.

**Dependency isolation.** Transitive dependencies are resolved by probing the extension's own directory and its `.deps.json`. Conflicting dependency versions across extensions are not supported: the first-loaded version wins, and a second extension requiring a different version may fail with a type-identity or strong-name mismatch.

Config-driven startup loading (`extensions:` key in `config.yaml`) and the `/reload` restart path are covered separately by ADR-009.

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

## Example: complete `.dmon/config.yaml`

```yaml
# dmon configuration
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
