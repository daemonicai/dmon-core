## 1. Package scaffold

- [x] 1.1 Create `tools/Dmon.Tools.WebSearch/Dmon.Tools.WebSearch.csproj` modelled on `Dmon.Tools.Dmail.csproj` (net10.0, `TreatWarningsAsErrors`, `IsPackable`, `MinVerTagPrefix=sdk-`, `PackageReadmeFile`); `PackageReference` to `Microsoft.Extensions.AI` and `ProjectReference` to `core/Dmon.Abstractions` only — **no** provider-package or provider-SDK reference.
- [x] 1.2 Add `tools/Dmon.Tools.WebSearch/README.md` describing the tool, the `AddAgentWebSearch(p => p.UseGemini("..."))` wiring, and the network-egress prompt.
- [x] 1.3 Add the project to the `tools/` `.slnx` and to `Everything.slnx`.

## 2. WebSearchExtension (sub-agent tool)

- [ ] 2.1 Implement `WebSearchExtension : IToolExtension` that takes an `IChatClientFactory` in its constructor and exposes a single `web_search` `AIFunction` (param `query`, description directing the model to ask the web a question). Set `Name`/`Description`.
- [ ] 2.2 Implement the call path: `await factory.CreateAsync(ct)`, one `GetResponseAsync(query, new ChatOptions { Tools = [new HostedWebSearchTool()] }, ct)`; make no HTTP request from the tool itself.
- [ ] 2.3 Implement the provider-agnostic projection to `{ answer, sources[] }`: `answer` from response text; one source per `UriContent` inside `WebSearchToolResultContent` (carry uri + title from `AdditionalProperties`); empty source list when none present. Format a compact, deterministic string result.
- [ ] 2.4 Implement `Evaluate` to return a prompt result for `web_search` (network egress).
- [ ] 2.5 Wrap the hosted call so failures (missing key → `InvalidOperationException` naming the env var, transport/provider errors) return a short error string instead of throwing out of the tool.

## 3. Composition verb

- [ ] 3.1 Add `AddAgentWebSearch<T>(this T registration, Action<IProviderRegistration> configure) where T : IToolRegistration` in the `Dmon.Hosting` namespace → `registration.AddToolExtension(new WebSearchExtension(SubAgent.BuildClient(configure)))`.
- [ ] 3.2 Confirm structural validation propagates: a configure action with zero/multiple providers or no model throws `InvalidOperationException` at build (covered by `SubAgent.BuildClient`); no extra validation in the verb.

## 4. Tests

- [ ] 4.1 Create `test/Dmon.Tools.WebSearch.Tests` (xunit) and add it to the test slnx/Everything.slnx.
- [ ] 4.2 Test the projection against a stubbed `ChatResponse` containing `TextContent` + `WebSearchToolResultContent`/`UriContent` → asserts `{ answer, sources[] }`; plus the no-sources and provider-agnostic (same content shape) cases — using a fake `IChatClient`/`IChatClientFactory`, no network.
- [ ] 4.3 Test that the tool issues exactly one `GetResponseAsync` whose `ChatOptions.Tools` contains a `HostedWebSearchTool`, and makes no HTTP call.
- [ ] 4.4 Test `Evaluate` returns a prompt result for `web_search`.
- [ ] 4.5 Test graceful failure: a throwing fake client yields a short error string (and a missing-key error names the env var).
- [ ] 4.6 Test `AddAgentWebSearch` registers exactly one `web_search` tool, and that a malformed configure action fails the build with `InvalidOperationException`.
- [ ] 4.7 Test that lazy resolution holds: registering the tool with an absent brain key does not throw at registration/build (only on invocation).

## 5. Sample wiring and gates

- [ ] 5.1 Add a sample composition root under `samples/` (or extend an existing one) showing a local driving provider + `AddAgentWebSearch(p => p.UseGemini("gemini-2.5-flash"))`, matching the design's `Dmon.cs` shape.
- [ ] 5.2 Run gates: `make build` clean (TreatWarningsAsErrors), `make test` green (new + existing), `openspec validate websearch-tool --strict`.
