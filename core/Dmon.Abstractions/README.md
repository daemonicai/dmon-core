# Dmon.Abstractions

Extension and provider contracts for the dmon coding agent.

Provides the interfaces that extension authors implement:
- `IDmonExtension` — the entry point for a NuGet-packaged extension; exposes `AIFunction` tools via `Microsoft.Extensions.AI`
- `IProviderFactory` — contract for LLM provider plugins (ADR-007)
- Supporting value types and abstractions

Depends on `Dmon.Protocol` for shared DTOs and `Microsoft.Extensions.AI` for `IChatClient`/`AIFunction`.

Licensed under the [Mozilla Public License 2.0](https://www.mozilla.org/en-US/MPL/2.0/).
