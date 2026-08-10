# Documentation drift pass

**Status:** open — three items, all small, deliberately bundled
**Surfaced:** 2026-08-04/05, across sections 1–4 of `dmon-home-foundations`
**Severity:** low individually; the pattern is the point

## The items

### 1. A hard-coded ADR count, stored in two files

`CLAUDE.md` and the `adr-index` skill's frontmatter both state how many accepted
ADRs exist. It has been **wrong on two of its last two touches** (it previously
carried the *file* count, not the accepted count).

**The fix is deletion, not maintenance.** It is a derived value stored in two
places, and no reader benefits from the number. Replace with "Summaries of every
accepted ADR" and stop re-deriving it.

### 2. ADR-013's status disagrees with itself

The `adr-index` row says *"(Superseded by ADR-022.)"*; `docs/adrs/ADR-013-agent-profiles.md`
reads `**Status:** Accepted`. The ADR-009 pair agrees across row and file;
ADR-013 does not.

### 3. `.serena/memories/adrs.md` is an orphan

A tracked six-ADR summary with **no consumer** — the agent definitions state
there is no Serena MCP in this project. A derived duplicate nobody maintains and
no reader benefits from. Same argument as item 1, and worse: an orphaned ADR
summary is the exact trap the `adr-index` nearly became, because nothing will
ever prompt anyone to update it.

## The pattern behind all three

Every one is **a fact stored in more than one place, where only one place is
ever updated.** This project has now been bitten by that shape repeatedly — a
shared test double whose default collided with another default, a duplicated
grace-period literal, a defect signature written twice and already inconsistent.

The generalisation worth keeping: **scope a sweep by where the fact is
duplicated, not by where the fix was.** A clean `grep` for stale wording cannot
find a *missing addition*, which is how a correct search once coexisted with an
index row that omitted an entire decision.

## Related, by design rather than by drift

`.claude/agents/{worker,reviewer,supervisor}.md` restate binding ADR constraints
as review checklists. Nothing is stale — but they duplicate ADR content by
design and are maintained by `dmons:update-scaffold` rather than by change work,
so they cannot drift *from* a change and will drift *behind* one silently.

This needs its own rule rather than an extension of the index rule: the trigger
is **a binding .NET-side ADR changing**, not any ADR gaining a decision.
