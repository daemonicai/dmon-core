## Context

A dmon agent's driving model is often **local and offline** (Ollama, llama.cpp, OMLX). Such a model cannot search the web or read pages. We want to give it web access *as a tool* without making the local model itself network-aware, and without flooding its small context window with raw search results.

A live spike on 2026-06-16 (recorded in memory `project-websearch-hosted-grounding-spike`) verified the load-bearing assumption against our pinned dependencies (`Microsoft.Extensions.AI` 10.5.2, `Anthropic` 12.24.1, `GeminiDotnet.Extensions.AI` 0.25.0):

- `HostedWebSearchTool` is defined in M.E.AI.Abstractions 10.5.2 and honoured by both the Gemini bridge (→ `set_GoogleSearch` + grounding metadata) and the Anthropic bridge (→ `WebSearchTool20250305/20260209` + citation locations). **No provider-package changes are required.**
- A single non-streaming `GetResponseAsync` against `gemini-2.5-flash` with `HostedWebSearchTool` ran the full search→read→synthesise loop server-side and returned fresh, post-cutoff information for ~565 tokens.
- M.E.AI normalises the result into **typed content parts**: `TextContent` (answer) + `WebSearchToolCallContent` (queries run) + `WebSearchToolResultContent` whose outputs are `UriContent { uri, mediaType, additionalProperties.title }`. These are M.E.AI types, not provider types — so projecting `{ answer, sources[] }` is provider-agnostic.

The sub-agent seam this design builds on already exists in `Dmon.Abstractions` (`IChatClientFactory`, `SubAgent.BuildClient`, `SubAgentProviderRegistration`, the `IProviderRegistration` provider verbs) — the `sub-agent-extensions` standing spec even names `dmon-websearch` as its worked example.

## Goals / Non-Goals

**Goals:**

- A `web_search` tool that lets an offline driving agent ask the web a question and receive a synthesised, citation-bearing answer sized for a small context window.
- Implement it as a sub-agent `IToolExtension` (ADR-010) that delegates search + synthesis to a hosted search-capable model via `HostedWebSearchTool`.
- Keep `Dmon.Tools.WebSearch` **vendor-SDK-free** (references only `Dmon.Abstractions`); the search brain's provider is supplied by the composition root.
- Reuse the existing sub-agent seam with **zero new infrastructure** — the only net-new type is `WebSearchExtension`.
- Provider-agnostic result projection (reads M.E.AI content types only).

**Non-Goals (V1):**

- A separate `read_url`/fetch tool, or the agent orchestrating a search→fetch two-beat (the hosted model does fetching internally).
- Resolving Gemini's `grounding-api-redirect` URLs to canonical source URLs.
- A runtime `dmon-websearch.model` config-section override (`<adapter>/<model-id>`); the model is fixed in the composition root for V1.
- An optional provider-bundling convenience package (e.g. a Gemini-defaulting `AddAgentWebSearch()` no-arg overload).
- Provider-native grounding on the *driving* model (breaks for local agents — explicitly rejected).

## Decisions

### Decision 1: Sub-agent tool over a raw search-API client

`web_search` constructs a scoped, single-turn `IChatClient` against a hosted model and passes `HostedWebSearchTool`; the hosted model searches, reads, and synthesises server-side.

- **Why**: the offline agent gets a digested answer + citations, not raw SERP JSON; it works regardless of the driving provider; and it adds no new external dependency (the brain's SDK rides in via a provider package already in the composition root). This is exactly the ADR-010 in-scope sub-agent pattern.
- **Alternatives**: (a) a hosted search API (Brave/Tavily/Exa) behind an HTTP client like `Dmon.Tools.Dmail` — viable, but returns unsynthesised results and adds a vendor dependency + key; (b) provider-native grounding on the driving model — rejected, only works when the agent itself runs on a hosted, search-capable provider.

### Decision 2: Reuse the existing `IChatClientFactory` / `Action<IProviderRegistration>` seam

`AddAgentWebSearch(this T, Action<IProviderRegistration>)` → `r.AddToolExtension(new WebSearchExtension(SubAgent.BuildClient(configure)))`. `WebSearchExtension` captures the `IChatClientFactory` and calls `CreateAsync` per tool call.

- **Why**: the seam is built and spec-mandated (`sub-agent-extensions`). `IProviderRegistration` exposes only `Services` + `Configuration` — there is no path to `IProviderRegistry`, so independence is **structurally enforced**. `SubAgent.BuildClient` gives eager structural validation (one provider verb + a model) at compose time and lazy credential/client construction at first use.
- **Alternatives**: a `Func<IServiceProvider, IChatClient>` parameter (more flexible, allows `ChatClientBuilder` wrapping and non-provider clients) was considered and **rejected for V1** because it hands the tool the host's built container — re-opening the `IProviderRegistry` access that ADR-022 D6 deliberately closed. Adopting it would require a superseding/amending ADR; deferred until a concrete client-wrapping need exists, ideally via a registry-free scoped provider rather than the full container.

### Decision 3: Verb-only model selection for V1 (no runtime override)

The brain's model is fixed by the provider verb in the composition root (`p.UseGemini("gemini-2.5-flash")`). The tool does not read a `dmon-websearch.model` config section.

- **Why**: it sidesteps the one real tension in the design — the spec's `<adapter>/<model-id>` config form (`gemini/gemini-2.5-flash`) implies *runtime* provider selection from a string, which a vendor-SDK-free tool cannot honour without re-introducing a provider dependency or an adapter→verb registry. Verb-only keeps `Dmon.Tools.WebSearch` strictly `Abstractions`-only with zero resolution logic. `UseModel` already stores the same `<adapter>/<model>` string shape, so a future config-override change is additive.
- **Alternatives**: verb default + config override (honours the spec's config-section requirement now) — deferred to a follow-up change to avoid the adapter-resolution work and the packaging compromise in V1.

### Decision 4: Structured `{ answer, sources[] }` projection from M.E.AI content parts

The tool projects `response.Text` as the answer and each `UriContent` inside `WebSearchToolResultContent` into a source `{ url, title }`. The serialised tool result is deterministic and small.

- **Why**: citations arrive as typed parts, not prose, so no scraping; reading only M.E.AI types makes the projection provider-agnostic (Gemini and Anthropic both map into these parts).
- **Note**: Gemini returns `vertexaisearch.cloud.google.com/grounding-api-redirect/...` URLs with the real domain in `title`. V1 surfaces the redirect URL + title as-is; canonical-URL resolution is a non-goal.

### Decision 5: Prompt on first use

`WebSearchExtension.Evaluate` returns a prompt result for `web_search`.

- **Why**: the query egresses to a hosted provider — a privacy-relevant network action — even when the driving agent is local/private. ADR-006 is conservative for network egress. The user can allow-for-session.

## Risks / Trade-offs

- **Anthropic path is type-verified but not live-tested** (no `ANTHROPIC_API_KEY` at spike time) → mitigate: the projection reads only shared M.E.AI content types; add a provider-agnostic projection unit test, and gate any Anthropic-specific claim behind a live check before relying on it.
- **Cost/latency rises vs a raw search API** — each call is a full hosted agentic round-trip → mitigate: default the brain to a cheap, fast model (`gemini-2.5-flash`/`-flash-lite`); the cost is incurred only on explicit `web_search` calls.
- **Redirect URLs reduce source usefulness** — the agent can't directly fetch a `grounding-api-redirect` link → accepted for V1 (title carries the domain); canonical resolution is a follow-up.
- **Verb-only model is less flexible** than runtime config → accepted; additive to add later without breaking the verb.
- **Hosted failure must degrade gracefully** — a thrown exception in a tool would break the agent loop → mitigate: catch and return a short error string (mirrors `Dmon.Tools.Dmail`).

## Open Questions

- None blocking V1. Deferred to follow-ups: the `read_url` companion tool, the config-section model override, redirect-URL canonicalisation, and the optional provider-bundling convenience package.
