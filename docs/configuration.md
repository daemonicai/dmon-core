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
| `extensions` | list | `[]` | Extensions to load at startup (see [Extension configuration](#extension-configuration)). |
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

## Extension configuration

Active extensions are declared in `config.yaml` and loaded automatically at `Dmon.Core` startup. This is the concrete meaning of ADR-002's "approved at project/global scope": presence in config is the approval. See [ADR-009](./adrs/ADR-009-config-driven-extension-loading.md) for the full decision and rationale.

### Schema

Each entry in the `extensions` list has a required `source` field and optional per-entry settings:

```yaml
extensions:
  - source: "nuget:Acme.Tools/1.2.3"   # NuGet package, pinned version
  - source: "nuget:Acme.Analytics"      # NuGet package, unpinned (refused at add-time; see below)
  - source: "./ext/MyExtension.dll"     # local assembly path
  - source: "./scripts/helpers.csx"     # .csx script path
```

`source` accepts three forms:

| Form | Syntax | Notes |
|------|--------|-------|
| NuGet package | `nuget:<id>/<version>` | Version must be pinned. Unpinned sources are refused at add-time. |
| Assembly path | `./relative/path.dll` or an absolute path | Resolved relative to the config file's directory. |
| Script path | `./relative/path.csx` or an absolute path | Loaded via the `.csx` script host. |

### Scope and merge

The `extensions` list can appear in **both** config scopes:

- **Project:** `./.dmon/config.yaml` — checked into the repo; sets project-specific extensions.
- **User:** `~/.dmon/config.yaml` — machine-wide personal extensions.

At startup the **effective set** is the **union** of both lists. The union is computed by reading both files directly (not via `IConfiguration` array layering, which replaces arrays by index). Deduplication:

- **Paths:** deduplicated by normalized path (lowercased for the dedup key, trailing separator stripped).
- **NuGet:** deduplicated by `id+version`; a different inline version is treated as a distinct source.
- Where the same source appears in both scopes, the **project entry's per-entry settings win**.

**Load order is deterministic:** user entries are processed first (in file order), then project entries (in file order). This matters for first-writer-wins dependency resolution (see [ADR-008](./adrs/ADR-008-extension-load-context.md)).

### Security gate and trust

**Config presence = trust.** An entry in config is a previously-approved source and loads at startup **without** an interactive permission prompt. The [ADR-006](./adrs/ADR-006-permission-model.md) security gate fires at **add time** — when a source is written to config — not on every startup. Removing the entry revokes the trust.

### Adding and removing extensions (edit-only model)

There is **no ephemeral in-memory-only extension load**. Being loaded and being in config are the same thing. The workflow is:

1. **To activate:** write the source to a config scope, then run `/reload`.
2. **To deactivate:** remove the source from config, then run `/reload`.

The `extension.load` RPC operation (exposed in the terminal host as `/load <source> [project|user]`, where scope defaults to `project`) automates the config-write step:

- It runs the ADR-006 add-time gate before writing anything. For `nuget:` sources this fetches and security-analyses the package; it **refuses to write if the source cannot be fetched** or if the version is unpinned. Local `.csx` and assembly paths are written directly.
- On success it appends the entry to the chosen scope's `extensions:` list and reports that a reload is required. It does **not** load the extension into the running process.
- The read-only `extension.analyze` tool produces the security report that the agent or user reviews before issuing `/load`.

The add-time gate applies to the `/load` path only. **Hand-editing `config.yaml` bypasses it** — config edits are inherently trusted. A manually added entry is not analysed at write time; an unpinned or unfetchable NuGet entry added by hand is simply logged and skipped at the next startup (see [Startup behaviour](#startup-behaviour)).

### Startup behaviour

A failing entry is **logged and skipped**; startup continues with the remaining entries. No entry failure aborts the process.

### `/reload` — process restart

`/reload` (terminal host) restarts the `Dmon.Core` process:

1. Stops the current core process.
2. Spawns a fresh one, which re-reads both `config.yaml` files and loads the effective extension set.
3. Re-binds the terminal's stdio read/write loop to the new process.
4. Re-opens the active session directory against the new process (sends `session.load` for the tracked session so the fresh process re-acquires its lock).

`/reload` runs **only between turns** and is rejected if a streaming turn is in progress.

**Important:** `/reload` does **not** currently restore the in-progress conversation's message history into the fresh process. The new process re-binds to the same session directory and re-acquires its lock, but conversation-history continuity across restart is a deferred follow-up — the core does not yet persist and rehydrate turn history. After a reload, the session is re-attached but prior message context is not replayed to the model.

---

## Extension loading — implementation notes

NuGet/local-assembly extensions load into the **Default `AssemblyLoadContext`** (`AssemblyLoadContext.Default`). There is no per-extension collectible context. See [ADR-008](./adrs/ADR-008-extension-load-context.md) for the rationale.

**Unload semantics.** `extension.unload <name>` (and the corresponding `ExtensionService.Unload` call) is a **deregister-only** operation: the extension's tools are removed from the registry and are no longer offered to the LLM, but the extension's assembly remains resident in the process. To reclaim the assembly — or to pick up a changed extension — restart the `Dmon.Core` process. This is exactly what `/reload` does.

**Dependency isolation.** Transitive dependencies are resolved by probing the extension's own directory and its `.deps.json`. Conflicting dependency versions across extensions are not supported: the first-loaded version wins, and a second extension requiring a different version may fail with a type-identity or strong-name mismatch. Because load order is deterministic (user-then-project, each in file order), first-writer-wins is predictable.

### ADR cross-reference

The config-driven extension model is governed by four ADRs:

| ADR | Role |
|-----|------|
| [ADR-009](./adrs/ADR-009-config-driven-extension-loading.md) | **Primary.** Extensions declared in `config.yaml`; loaded at startup; `/reload` = restart-to-reload. Backs this entire feature. |
| [ADR-008](./adrs/ADR-008-extension-load-context.md) | **Prerequisite.** Default-context loading and restart-as-reclaim are what make config-driven reload coherent. `/reload` is the user-facing trigger for the restart-to-reclaim mechanic ADR-008 specifies. |
| [ADR-002](./adrs/ADR-002-extension-tool-contract.md) | **Contract.** The `IDmonExtension`/`AIFunction` interface all extensions implement. ADR-009 gives concrete meaning to ADR-002's "approved at project/global scope": presence in `config.yaml` is the approval. |
| [ADR-006](./adrs/ADR-006-permission-model.md) | **Security gate.** Conservative permission model; under ADR-009 the gate fires at **add time** (writing a source to config), not on every startup. |

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

extensions:
  - source: "nuget:Acme.Tools/1.2.3"   # pinned NuGet extension
  - source: "./scripts/helpers.csx"     # project-local .csx script

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
