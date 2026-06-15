# dmon — Configuration Reference

dmon reads configuration from up to three YAML sources (in order, later overrides earlier):

1. `~/.dmon/config.yaml` — user-global
2. `.dmon/config.yaml` — project-local
3. `.dmon/config.local.yaml` — project-local override (untracked; for personal overrides)

All files are optional. When none exists and `.dmon/` has not been bootstrapped, the core starts with sensible defaults.

> **Extensions and middleware are not declared here.** They are composed at compile time in `Dmon.cs` via `#:package` directives and builder calls. See the [`composition-root-hosting`](./adrs/ADR-019-composition-root-hosting.md) capability for details. `config.yaml` is for settings only.

---

## Configuration keys

### Top-level keys

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `sessionStore` | string | `"local"` | Where session data is stored. `"local"` = `<project>/.dmon/sessions/`, `"global"` = `~/.dmon/sessions/`, or an absolute path. |
| `providers` | object | — | Provider definitions (see [Provider configuration](#provider-configuration)). |
| `profiles` | map | `{}` | Named agent profile bundles (see [Agent profiles](#agent-profiles)). |
| `defaultProfile` | string | `"coding"` | Name of the profile to select when no per-session profile is requested (see [Agent profiles](#agent-profiles)). |

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

## Command-extension configuration

A command (sub-agent) extension reads its own settings from a **peer top-level `commands:<name>` section**, where `<name>` is the extension's `IDmonExtension.Name` (e.g. `dmon-websearch`).

```yaml
commands:
  dmon-websearch:
    model: gemini/gemini-2.5-flash   # <adapter>/<model-id>
    timeout: "30"                    # arbitrary fields are permitted
```

**Reading the section.** Extensions read their section directly from the `IConfiguration` injected via `IServiceProvider`:

```csharp
IConfigurationSection section = configuration.GetSection($"commands:{Name}");
string? model = section["model"];
```

**Layering behaviour.** The standard last-wins precedence applies: `~/.dmon/config.yaml` < `./.dmon/config.yaml` < `./.dmon/config.local.yaml`.

**Scope.** The section is purely for extension settings. Arbitrary fields are allowed; the only reserved field is `model`, which carries the provider/model identity as `<adapter>/<model-id>`. Extensions must not rely on the section being present — absence means the extension's defaults apply.

**ADR cross-reference.** This convention is governed by [ADR-010](./adrs/ADR-010-sub-agent-extensions.md).

---

## Middleware configuration

A middleware extension reads its own settings from a **top-level `middleware:<ClassName>` section**, where `<ClassName>` is the middleware class's unqualified name. The key lookup is **case-insensitive** (standard `IConfiguration` key matching).

```yaml
middleware:
  TokenLimitMiddleware:
    priority: 50          # optional — overrides [DmonMiddleware(Priority = ...)]
    maxTokens: "4096"     # arbitrary fields are permitted
    logRequests: "true"
```

**Reading the section.** Middleware reads its section from the `IConfigurationRoot` injected via `IServiceProvider`:

```csharp
IConfigurationRoot root = serviceProvider.GetRequiredService<IConfigurationRoot>();
IConfigurationSection section = root.GetSection($"middleware:{nameof(TokenLimitMiddleware)}");
string? maxTokens = section["maxTokens"];
```

`IConfigurationRoot` is registered in the host DI container alongside `IConfiguration`; resolve it with `GetRequiredService<IConfigurationRoot>()`.

**Priority override.** The optional `priority` field (int) overrides the value declared on the `[DmonMiddleware]` attribute. When the field is present, it becomes the middleware's effective priority used for pipeline ordering. When absent, the attribute value applies.

**Layering behaviour.** The same last-wins precedence applies: `~/.dmon/config.yaml` < `./.dmon/config.yaml` < `./.dmon/config.local.yaml`.

**Scope.** Arbitrary fields are permitted in the section. No field name is reserved beyond `priority`. Middleware must not require the section to be present — absence means the middleware's defaults apply.

**No hot-reload.** Middleware is constructed once at startup and the pipeline is held for the session. Apply middleware changes by restarting the core process, which rebuilds the pipeline.

---

## Agent profiles

Agent profiles are named bundles that define an agent's identity (persona), asset directory behaviour, and permission posture for a session. Governed by [ADR-013](./adrs/ADR-013-agent-profiles.md).

### Built-in `coding` profile

The `coding` profile is always available without any config. It uses the standard coding persona, does not provision an asset directory, and runs with the `coding` permission mode (ADR-006 behaviour). **When no profile is configured and no per-session profile is requested, the built-in `coding` profile applies — the default behaviour is unchanged.**

### Profile schema

The `profiles` key is a YAML map where each key is the profile name:

```yaml
profiles:
  helper:
    persona: "You are a helpful research assistant."  # inline persona text
    assets: false                                     # default false
    permissionMode: coding                            # coding | sandbox

  writer:
    personaFile: ./personas/writer.md    # OR a path to a persona file
    assets: true
    permissionMode: sandbox
```

Each entry supports these fields:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `persona` | string | One of `persona` or `personaFile` | Inline persona text injected into the system prompt. |
| `personaFile` | string (path) | One of `persona` or `personaFile` | Path to a file whose contents are used as the persona. Resolved relative to the config file that declares it. |
| `assets` | bool | No (default `false`) | When `true`, a per-session `assets/<session_id>/` directory is provisioned under the workspace root. Required when `permissionMode` is `sandbox`. |
| `permissionMode` | `coding` \| `sandbox` | No (default `coding`) | Permission posture for the session (see [Permission modes](#permission-modes)). |

Exactly one of `persona` or `personaFile` must be present. Specifying both or neither is a configuration error that aborts session start with an actionable message.

### `defaultProfile`

The `defaultProfile` key names the profile to use when no per-session profile is requested:

```yaml
defaultProfile: helper
```

When absent, the built-in `coding` profile applies.

### Selection precedence

Profile selection for a session follows this order (highest to lowest):

1. **Per-session `profile`** — passed when creating the session (consumed by the remote-session-gateway; not yet exposed over the wire in the current release).
2. **`defaultProfile`** — the configured default, if present.
3. **Built-in `coding`** — when neither of the above applies.

A selected profile name that does not exist in the effective set (config union built-in) is a hard error — no session is created and an actionable message names the unknown profile and the available alternatives.

### Scope and merge

The `profiles` map and `defaultProfile` key follow the two-scope merge across user and project config files:

- **User:** `~/.dmon/config.yaml`
- **Project:** `./.dmon/config.yaml`

The **effective set** is the **union** of both maps. Where the same profile name appears in both scopes, the **project entry's fields win** (case-insensitive name comparison). For `defaultProfile`, the project value wins when both scopes declare one; if only one scope declares it, that value is used.

### Permission modes

| Mode | Behaviour |
|------|-----------|
| `coding` | ADR-006 default: CWD-subtree reads are implicit; all writes and bash commands require a per-operation prompt. |
| `sandbox` | Identical to `coding` except that write/edit operations whose normalised target is within `assets/<session_id>/` are **implicitly allowed** (no prompt). The configured denylist and all other gate behaviour are unchanged — a denylist entry inside the asset dir still denies. |

`sandbox` requires `assets: true`. Setting `permissionMode: sandbox` with `assets: false` is an incoherence error that aborts session start.

### Example

```yaml
defaultProfile: coding   # optional — coding is the default

profiles:
  coding:                # overrides the built-in coding profile (optional)
    persona: "You are a coding agent specialised in Go."
    assets: false
    permissionMode: coding

  writer:
    personaFile: ./personas/writer.md
    assets: true
    permissionMode: sandbox
```

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
