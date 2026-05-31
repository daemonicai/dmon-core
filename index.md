---
layout: home
title: dmon-core
tagline: the agent core · .NET 10
lead: >-
  A .NET-native coding agent inspired by Pi. The agent core runs as a separate
  process over JSONL/stdio; console/TUI and Avalonia desktop hosts are planned.
repo: daemonicai/dmon-core
ctas:
  - { label: "View on GitHub →", href: "https://github.com/daemonicai/dmon-core", primary: true }
  - { label: "dcli — the terminal UI", href: "https://daemonicai.github.io/dcli/" }
features:
  - icon: "◈"
    title: Self-extensible via NuGet
    body: Extensions ship as NuGet packages tagged dmon-extension. The agent can search, analyse, and load them at runtime.
  - icon: "🛡"
    title: Source-gated security
    body: Before loading, dmon fetches an extension's source at the published commit and runs an LLM security analysis. No source, no load.
  - icon: "⚖"
    title: Curated tool output
    body: Tools return a ranked shortlist, not a raw API dump — every token a tool returns is consumed by the model's reasoning step.
  - icon: "🔌"
    title: Provider-agnostic
    body: LLM access goes through Microsoft.Extensions.AI (IChatClient) — Anthropic, OpenAI, Gemini, or a local model.
  - icon: "↹"
    title: JSONL over stdio
    body: A clean RPC protocol between the core and its host surfaces, with append-only JSONL session storage.
  - icon: "🔐"
    title: Conservative by default
    body: A tiered permission model prompts before consequential actions rather than asking forgiveness.
---

## Quick start

```bash
make build       # build all projects
dotnet test      # run all tests
```

Set at least one provider key before running:

```bash
export ANTHROPIC_API_KEY=sk-ant-...
# or OPENAI_API_KEY / GEMINI_API_KEY
```

## Extensions

dmon is self-extensible. Extensions are distributed as NuGet packages tagged
`dmon-extension`, and the agent works with them at runtime:

```
extension.search  <query>    # find extensions on NuGet
extension.readme  <id>       # read a package README excerpt
extension.analyze <id>       # security-analyse source before loading
extension.load    <id>       # load a confirmed extension
```

Extensions implement `IDaemonExtension` (from `Dmon.Extensions`) and expose tools as
`AIFunction` instances. [dmon-websearch](https://daemonicai.github.io/dmon-websearch/)
is the reference out-of-tree extension.

## Architecture

- **RPC protocol** — JSONL over stdio
- **LLM abstraction** — `Microsoft.Extensions.AI` (`IChatClient`)
- **Extension model** — `IDaemonExtension` + `AIFunction`
- **Session storage** — relocatable directory, append-only JSONL
- **Permission model** — tiered prompts, conservative by default

The architecture decisions are recorded as ADRs in the repo. See the
[README](https://github.com/daemonicai/dmon-core#readme) for the full picture.
