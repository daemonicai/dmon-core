## Why

A dmon agent driven by a **local/offline model** (llama.cpp, Ollama, OMLX) has no way to reach the web: the local model can neither search nor read pages, and adding a raw search-API client would dump unranked SERP JSON into a small context window. We can instead give the offline agent web access *as a tool* by delegating the whole search-read-synthesise loop to a **hosted, search-capable model**, handing back only a digested, citation-bearing answer. A live spike (2026-06-16) proved this works today through `Microsoft.Extensions.AI`'s `HostedWebSearchTool` against our pinned Gemini provider — with **zero changes to any provider package**.

## What Changes

- **New tool package `tools/Dmon.Tools.WebSearch`** exposing a single agent tool, `web_search`, via `IToolExtension`. The package references **only `Dmon.Abstractions`** — it carries no vendor SDK.
- **`WebSearchExtension` is a sub-agent tool** (ADR-010 / `sub-agent-extensions`): it captures an `IChatClientFactory` at construction and, per call, builds a scoped single-turn `IChatClient` against a hosted search-capable provider, invoking it with `ChatOptions.Tools = [new HostedWebSearchTool()]`. The driving agent never touches the network.
- **Composition verb `AddAgentWebSearch(this T, Action<IProviderRegistration>)`** (on `IToolRegistration`), wiring the search brain via the existing provider verbs, e.g. `AddAgentWebSearch(p => p.UseGemini("gemini-2.5-flash"))`. The model is fixed in the composition root for V1 (no runtime config override).
- **Structured result contract**: `web_search` returns a normalised `{ answer, sources[] }` projected from the typed `TextContent` + `WebSearchToolResultContent` → `UriContent` parts that M.E.AI surfaces — no prose-parsing.
- **Permission posture**: `web_search` prompts on first use (network egress — the query leaves to a hosted provider, even for a local driving agent).
- **Provider-agnostic by construction**: because the result projection reads M.E.AI content types (not provider types), the same tool works with any hosted provider whose `IChatClient` honours `HostedWebSearchTool` (Gemini verified live; Anthropic type-verified).

Out of scope for V1 (noted for design): a `read_url`/fetch tool; resolving Gemini's grounding-redirect URLs to canonical sources; a `dmon-websearch.model` runtime config-section override; an optional provider-bundling convenience package.

## Capabilities

### New Capabilities
- `websearch-tool`: the `web_search` agent tool — a sub-agent `IToolExtension` that delegates web search and synthesis to a hosted, search-capable model via `HostedWebSearchTool`, returns a structured `{ answer, sources[] }`, registers via `AddAgentWebSearch`, and prompts on first use.

### Modified Capabilities
<!-- None. The change consumes the existing sub-agent-extensions seam (IChatClientFactory / SubAgent.BuildClient / IProviderRegistration verbs) and the permission-model contract without altering their requirements. The verb-only model decision deliberately avoids exercising sub-agent-extensions' config-section requirement in V1. -->

## Impact

- **New package**: `tools/Dmon.Tools.WebSearch` (+ test project `test/Dmon.Tools.WebSearch.Tests`), added to the `tools/` `.slnx` and `Everything.slnx`; lockstep on the protocol version line (ADR-024).
- **Reuses, does not change**: `Dmon.Abstractions` sub-agent seam (`IChatClientFactory`, `SubAgent.BuildClient`, `SubAgentProviderRegistration`, `IProviderRegistration` verbs), the provider packages (`Dmon.Providers.Gemini`/`.Anthropic`), and the permission-model `Evaluate` contract.
- **Standing specs touched at archive**: `monorepo-layout` (new tool bucket member); new `websearch-tool` standing spec.
- **Dependencies**: no new external dependencies in the tool package itself; the search brain's SDK arrives via whichever provider package the composition root references.
- **Binding ADRs honoured**: ADR-001 (M.E.AI `IChatClient`), ADR-010 (sub-agent in scope), ADR-022/023 (registration facets, granular tool package, provider-agnostic sub-agent tools), ADR-006 (conservative permission).
