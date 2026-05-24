# dmon

A .NET-native coding agent inspired by [Pi](https://github.com/earendil-works/pi). Written in C# on .NET 10. The agent core runs as a separate process over JSONL/stdio; two host surfaces are planned: a console/TUI host and an Avalonia desktop host.

See [`coding-agent-brief.md`](./coding-agent-brief.md) for the full vision and [`CLAUDE.md`](./CLAUDE.md) for project conventions.

---

## Quick start

```bash
make build       # build all projects
dotnet test      # run all tests
```

Set at least one provider key before running:

```bash
export ANTHROPIC_API_KEY=sk-ant-...
# or
export OPENAI_API_KEY=sk-...
# or
export GEMINI_API_KEY=...
```

---

## Extensions

dmon is self-extensible via NuGet. Extensions are distributed as NuGet packages tagged `dmon-extension`. The agent can search for, analyse, and load extensions at runtime:

```
extension.search  <query>    # find extensions on NuGet
extension.readme  <id>       # read a package README excerpt
extension.analyze <id>       # security-analyse source before loading
extension.load    <id>       # load a confirmed extension (RPC command)
```

Extensions implement `IDaemonExtension` (from `Dmon.Extensions`) and expose tools as `AIFunction` instances. See [`docs/adrs/ADR-002-extension-tool-contract.md`](./docs/adrs/ADR-002-extension-tool-contract.md) for the contract.

### Security

Before any extension is loaded, dmon fetches its source at the exact published commit and runs an LLM security analysis. Extensions without a `<repository url commit>` in their nuspec are refused unconditionally — source availability is non-negotiable.

---

## Tool design principles

Tools in dmon (both built-in and extension-provided) follow a **curated output** principle:

**Return a ranked shortlist, not a raw API dump.**

When a tool calls an external API (NuGet search, GitHub, etc.), it should:
- Filter to results relevant to the query
- Rank by signal quality (downloads, recency, stars)
- Cap output at a small number of results (≤5 for search tools)
- Present findings in a compact, readable format

This is not cosmetic. Every token the tool returns is consumed by the LLM's reasoning step. A tool that dumps 50 raw JSON results forces the model to do expensive in-context filtering on every invocation. A tool that returns 5 curated results with the key signals already extracted costs a fraction of that — and produces better decisions.

**The `readme_available` flag pattern.**

When a tool has optional deeper detail (e.g. a package README), it should not fetch that detail eagerly. Instead, expose a flag (`readme_available: true/false`) and provide a separate tool for the drill-down. This lets the agent fetch detail only when it is actually needed to resolve ambiguity — most of the time it won't be.

**Provide good `Description` metadata.**

Extension tools are presented to the LLM as a list of available functions. The `Description` on each `AIFunction` is how the model decides whether to call it. A vague description ("does stuff with files") costs the model reasoning tokens to figure out what the tool does. A precise description ("reads a file at the given path and returns its UTF-8 content; errors if the file does not exist or is binary") gives the model exactly what it needs in one read.

When authoring an extension, write your `AIFunctionFactory.Create` descriptions as if you are writing documentation for a peer developer who has never seen the tool — because, from the LLM's perspective, that is exactly what you are doing.

---

## Architecture

- **RPC protocol:** JSONL over stdio — see [`docs/adrs/ADR-003-rpc-protocol.md`](./docs/adrs/ADR-003-rpc-protocol.md)
- **LLM abstraction:** `Microsoft.Extensions.AI` (`IChatClient`) — see [`docs/adrs/ADR-001-llm-abstraction.md`](./docs/adrs/ADR-001-llm-abstraction.md)
- **Extension model:** `IDaemonExtension` + `AIFunction` — see [`docs/adrs/ADR-002-extension-tool-contract.md`](./docs/adrs/ADR-002-extension-tool-contract.md)
- **Session storage:** relocatable directory, append-only JSONL — see [`docs/adrs/ADR-004-session-storage.md`](./docs/adrs/ADR-004-session-storage.md)
- **Permission model:** tiered prompts, conservative by default — see [`docs/adrs/ADR-006-permission-model.md`](./docs/adrs/ADR-006-permission-model.md)
