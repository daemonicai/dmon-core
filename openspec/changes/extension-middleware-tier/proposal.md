## Why

The `IChatClient` pipeline model in `Microsoft.Extensions.AI` is a first-class composition pattern: any `IChatClient` can wrap another, mutating the full request (`IEnumerable<ChatMessage>`) and response (`ChatResponse` / `IAsyncEnumerable<ChatResponseUpdate>`) on every turn. Tools operate only at the LLM's discretion; middleware operates unconditionally, enabling capabilities — semantic caching, context-window management, RAG injection, guardrails, cost enforcement, observability — that cannot be expressed as tools.

## What Changes

- Add `IDmonMiddleware` interface to the extension contract assembly, with a single method `IChatClient Wrap(IChatClient inner)`.
- Add `[DmonMiddleware]` attribute (with an `int Priority` property) that marks a class as a middleware extension and controls its position in the pipeline.
- Extend the extension loader to discover `IDmonMiddleware` implementations in `.csx` scripts and NuGet extension packages (alongside existing `IDmonExtension` tool discovery).
- Construct the `IChatClient` pipeline at agent startup by folding discovered middlewares over the base provider client in priority order: `middlewares.Aggregate(baseClient, (inner, m) => m.Wrap(inner))`.
- Add per-middleware configuration support: each middleware gets a named section in the config YAML (arbitrary fields); priority can be overridden in config.
- Host injects an `IServiceProvider` (containing `IConfigurationRoot`) into middleware at construction so middleware can pull its own config.
- **No hot-reload for middleware.** Middleware changes require a process restart. Tool hot-reload is unaffected.

## Capabilities

### New Capabilities

- `extension-middleware`: The middleware extension tier — `IDmonMiddleware` interface, `[DmonMiddleware]` attribute, pipeline construction logic, middleware configuration schema.

### Modified Capabilities

- `extension-model`: The extension contract assembly gains new public types (`IDmonMiddleware`, `DmonMiddlewareAttribute`). No breaking changes to `IDmonExtension`. Loader gains middleware discovery path.

## Impact

- **`Dmon.Contracts`** (extension contract assembly): new `IDmonMiddleware` interface and `DmonMiddlewareAttribute` class.
- **Agent core / extension loader**: middleware discovery alongside tool discovery; pipeline construction at startup.
- **Config schema**: new top-level `middleware` section with per-middleware named subsections (arbitrary fields + optional `priority` override).
- **No impact** on the RPC protocol, session storage, permission model, or provider configuration.
