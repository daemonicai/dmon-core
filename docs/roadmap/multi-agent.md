# Multi-Agent Orchestration

**Status:** 💭 Idea  
**Depends on:** Stable RPC surface, clear understanding of single-agent use cases

---

## What

Running and coordinating multiple dmon agent instances — either in parallel (fan-out) or in sequence (pipelines), with one agent able to spawn or communicate with others.

## Why

Some tasks are naturally parallel: review these ten files, run these three investigations at once, have a "critic" agent review the output of a "writer" agent. The process-isolated architecture means multiple agents are already possible at the OS level — the question is whether dmon orchestrates them or leaves that to the user.

## Ideas for what it includes

- **Agent spawning** — one agent can start a child agent with a given context and task, and receive its output.
- **Structured output passing** — agents communicate structured results, not just text.
- **Fan-out patterns** — distribute a list of tasks across N agent instances, collect results.
- **Critic/writer pattern** — a "writer" agent produces output, a "critic" agent reviews it, results are merged.
- **Shared session state** — agents within an orchestration can read from a shared session context.

## Notes

- The current process-per-agent model is a good foundation. The RPC surface already supports this in principle.
- `Microsoft.Extensions.AI` has multi-agent primitives — worth understanding what they give us before designing our own.
- Multi-agent is *interesting* but not what makes a single-agent coding tool good. Don't let this distract from V1.
- Explicitly out of scope for V1.
