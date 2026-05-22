# ADR-001: LLM Provider Abstraction

**Date:** 2026-05-22
**Status:** Accepted

## Context

daemon needs to support runtime switching between multiple LLM providers:

- **Local models:** oMLX (omlx.ai), Ollama, llama.cpp
- **Cloud models:** Claude (Anthropic), Gemini (Google), OpenAI

The original brief suggested Microsoft Agent Framework (MAF) for "LLM provider abstraction and tool-calling protocol", with a note to feel where the friction is before committing.

Two alternatives were evaluated:

1. **Microsoft Agent Framework** (`github.com/microsoft/agent-framework`) — designed primarily for multi-agent orchestration graphs. Provides provider abstraction as a side effect of its broader architecture.

2. **`Microsoft.Extensions.AI` (`IChatClient`)** — a lightweight, first-party .NET abstraction for LLM providers. Just an interface and middleware pipeline; no opinions about agent loops or orchestration.

Key observations:
- oMLX, Ollama, and llama.cpp all expose OpenAI-compatible REST endpoints (`/v1/chat/completions`). oMLX additionally exposes a native Anthropic `/v1/messages` endpoint.
- All target providers have `IChatClient` implementations available as NuGet packages.
- Multi-agent orchestration is explicitly out of scope for V1 — the primary value proposition of MAF.

## Decision

Use **`Microsoft.Extensions.AI` (`IChatClient`)** as the sole LLM provider abstraction layer. Do not take a dependency on MAF.

Provider coverage via NuGet:

| Provider       | Package                        | Protocol        |
|----------------|--------------------------------|-----------------|
| OpenAI         | `OpenAI` (official)            | OpenAI          |
| Ollama         | `OpenAI` + custom `baseUri`    | OpenAI-compat   |
| llama.cpp      | `OpenAI` + custom `baseUri`    | OpenAI-compat   |
| oMLX           | `OpenAI` or `Anthropic.SDK`    | OpenAI or Anthropic-compat |
| Claude         | `Anthropic.SDK` (community)    | Anthropic       |
| Gemini         | `GeminiDotnet.Extensions.AI`   | Gemini          |

Runtime switching is handled by a config-driven provider registry — a factory that resolves a provider name to an `IChatClient` instance. The agent loop holds a reference it can swap at runtime (supporting Pi-style hotkey cycling between providers).

**Mid-turn switching:** `model.set` and `model.cycle` issued while a turn is in flight do not interrupt the current LLM call. The new provider takes effect on the next turn (next `turn.submit`, `turn.steer`, or `turn.followUp`). The `providerSwitched` event carries `effectiveNextTurn: true` so the host can communicate this to the user. A user who wants to abandon the current call and switch immediately must issue `turn.abort` first, then `model.set`.

oMLX instances should be addressed via the Anthropic adapter where possible, so that local models and cloud Claude present an identical interface to the agent loop.

## Consequences

- **Agent loop stays in control of its own behaviour.** `IChatClient` is just an interface; the agent loop is not inside any framework's primitive.
- **Runtime switching is a ~20-line registry/factory.** No framework ceremony required.
- **All target providers covered.** Three adapter types (OpenAI, Anthropic, Gemini) cover the full provider matrix.
- **No door closed on Semantic Kernel.** SK uses `IChatClient` underneath; it can be layered on later without a breaking change.
- **MAF remains available if multi-agent orchestration becomes a V2 requirement.** The `IChatClient` abstraction is compatible.
- **Capability differences between providers are not abstracted.** Tool-calling support, context window size, and streaming behaviour vary by model. The registry should encode known capabilities per provider entry so the agent loop can adapt.
