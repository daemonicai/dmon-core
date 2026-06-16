# Dmon.Tools.WebSearch

A `web_search` tool for the [dmon](https://github.com/daemonicai/dmon-core) coding agent.

## What it does

Exposes a single `web_search` agent tool that accepts a natural-language `query` and returns a structured result containing a synthesised `answer` and a list of `sources` (each with a URI and title). The tool makes no direct HTTP request itself — it delegates the search and synthesis to a hosted, search-capable model (e.g. Gemini with grounding enabled) via the `IChatClientFactory` seam defined in `Dmon.Abstractions`.

## Result shape

```json
{
  "answer": "...",
  "sources": [
    { "uri": "https://...", "title": "..." }
  ]
}
```

When no sources are returned by the hosted model the `sources` array is empty.

## Wiring

Register the tool in your composition root (`Dmon.cs`) by calling `AddAgentWebSearch` from the `Dmon.Hosting` namespace and providing a provider configuration for the search-capable model:

```csharp
builder.AddAgentWebSearch(p => p.UseGemini("gemini-2.5-flash"));
```

The provider configuration is independent of the primary agent provider — the search brain is resolved lazily and does not affect startup if its API key is absent.

## Permissions

On first use the tool prompts the user for network-egress permission because the query is sent to a third-party hosted provider. The prompt is displayed once per session unless permission is revoked.

## Package dependencies

`Dmon.Tools.WebSearch` references only `Dmon.Abstractions` and `Microsoft.Extensions.AI`. It carries no provider SDK. The provider implementation (e.g. `Dmon.Providers.Gemini`) is supplied at the composition root.
