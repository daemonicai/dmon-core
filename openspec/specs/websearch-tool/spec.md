# websearch-tool

## Purpose

The `websearch-tool` capability defines a single agent tool, `web_search`, that lets the driving agent ask the web a natural-language question and receive a synthesised, citation-bearing answer. It is implemented as a sub-agent tool (ADR-010) that delegates to a hosted, search-capable model via a `HostedWebSearchTool`, projecting a provider-agnostic answer-and-sources result.

## Requirements

### Requirement: web_search tool surface

The system SHALL expose a single agent tool named `web_search` through an `IToolExtension` in the package `Dmon.Tools.WebSearch`. The tool SHALL accept a natural-language `query` and return a synthesised, citation-bearing answer. The package SHALL reference only `Dmon.Abstractions` (and `Microsoft.Extensions.AI`) and SHALL NOT reference any provider SDK or provider package directly.

#### Scenario: Tool is registered and discoverable
- **WHEN** a composition root registers the extension via `AddAgentWebSearch`
- **THEN** the agent's tool set includes exactly one tool named `web_search` whose description directs the model to ask the web a question and whose single required parameter is a `query` string

#### Scenario: Package carries no vendor SDK
- **WHEN** the `Dmon.Tools.WebSearch` project is inspected
- **THEN** its only first-party dependency is `Dmon.Abstractions`, and it has no `PackageReference` or `ProjectReference` to any `Dmon.Providers.*` package or provider SDK

### Requirement: Sub-agent delegation to a hosted search-capable model

`web_search` SHALL be implemented as a sub-agent tool (ADR-010): it SHALL obtain a scoped `IChatClient` only by invoking a captured `IChatClientFactory`, and SHALL fulfil each call with a single-turn request whose `ChatOptions.Tools` contains a `HostedWebSearchTool`. The tool SHALL NOT perform its own HTTP search or page fetching; the hosted model performs the search-read-synthesise loop server-side.

#### Scenario: Single hosted call per tool invocation
- **WHEN** the agent invokes `web_search` with a query
- **THEN** the extension calls `IChatClientFactory.CreateAsync` to get a client and issues one `GetResponseAsync` whose `ChatOptions.Tools` includes a `HostedWebSearchTool`

#### Scenario: No direct network egress from the tool
- **WHEN** `web_search` runs
- **THEN** the extension makes no HTTP request of its own; all web access occurs inside the hosted model's response to the single `GetResponseAsync` call

### Requirement: Structured answer-and-sources result

`web_search` SHALL return a normalised result containing the synthesised answer text and a list of sources, each with a URL and a title, projected from the response's content parts (`TextContent` for the answer; `WebSearchToolResultContent` whose outputs are `UriContent` for the sources). The projection SHALL read only `Microsoft.Extensions.AI` content types so that it is independent of which provider produced the response.

#### Scenario: Answer and sources projected from typed content
- **WHEN** the hosted model returns an assistant message containing `TextContent` and a `WebSearchToolResultContent` with one or more `UriContent` outputs
- **THEN** the tool result contains the text as the answer and one source per `UriContent`, each carrying the URI and its title

#### Scenario: Provider-agnostic projection
- **WHEN** the same content shape is produced by a different hosted provider's `IChatClient`
- **THEN** the projection yields the same `{ answer, sources[] }` structure without provider-specific branching

#### Scenario: Answer with no sources
- **WHEN** the hosted model returns a `TextContent` answer but no `WebSearchToolResultContent`
- **THEN** the tool returns the answer with an empty source list rather than failing

### Requirement: Composition via AddAgentWebSearch with a composition-root model

The system SHALL provide a verb `AddAgentWebSearch` on `IToolRegistration` that accepts an `Action<IProviderRegistration>` selecting the search brain, and constructs the extension over the resulting `IChatClientFactory` via the existing sub-agent seam. For V1 the model SHALL be fixed by the provider verb in the composition root; the tool SHALL NOT read a runtime model-override configuration section.

#### Scenario: Wiring a hosted brain in the composition root
- **WHEN** a `Dmon.cs` calls `AddAgentWebSearch(p => p.UseGemini("gemini-2.5-flash"))`
- **THEN** the extension is registered with an `IChatClientFactory` built from that isolated registration, selecting the Gemini provider and the `gemini-2.5-flash` model

#### Scenario: Malformed brain configuration fails at build
- **WHEN** `AddAgentWebSearch` is called with an action that selects no provider, multiple providers, or no model
- **THEN** the build fails immediately with an author-facing `InvalidOperationException`, before host startup

### Requirement: Independence and lazy credential resolution

The captured search-brain factory SHALL NOT read or mutate the host's `IProviderRegistry`; the driving agent's current provider and model SHALL be unaffected by `web_search`. Credential resolution and client construction SHALL be deferred to the first `web_search` call, so a registered-but-unused tool SHALL NOT block core startup when the brain's API key is absent.

#### Scenario: Primary agent provider untouched
- **WHEN** a local driving provider (e.g. Ollama) is active and `web_search` is invoked using a hosted Gemini brain
- **THEN** the agent's active provider and model remain the local provider, and the sub-agent client is constructed without touching `IProviderRegistry`

#### Scenario: Missing key does not block startup
- **WHEN** the search brain's API key environment variable is unset and the tool is never invoked
- **THEN** the core starts normally, and only an actual `web_search` call fails — with a message naming the missing environment variable

### Requirement: Network-egress permission and failure handling

`web_search` SHALL prompt for permission on first use because the query egresses to a hosted provider (`Evaluate` returns a prompt result), consistent with the conservative permission model. When the hosted call fails (network error, missing credentials, provider error), the tool SHALL return a concise human-readable error string rather than throwing out of the tool.

#### Scenario: First use prompts
- **WHEN** the agent invokes `web_search` and no prior allow decision covers it
- **THEN** `Evaluate` returns a prompt result and the call proceeds only after approval

#### Scenario: Hosted failure surfaces as a message
- **WHEN** the hosted `GetResponseAsync` throws (e.g. missing key or transport error)
- **THEN** `web_search` returns a short error string describing the failure and does not propagate the exception to the agent loop
