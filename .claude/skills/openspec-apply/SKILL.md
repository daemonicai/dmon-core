---
name: openspec-apply
description: The authoritative block-by-block mechanics for implementing a dmon OpenSpec change as the Architect — DEVLOG conventions, pre-flight checks, how to carve a section into blocks, the worker/reviewer inner loop, the gates that must pass before ticking a box, the per-section supervisor review, and the done criteria. Load at the start of /opsx:apply, when resuming a partly-applied change, or whenever you need the exact rule for carving a block, briefing a worker, closing a section, or committing.
---

# OpenSpec apply — the mechanics

This file is **authoritative** for the apply loop. The roles, the "stop and ask"
list, and the standing Rules live in the root `CLAUDE.md` and always apply; this
file is the procedure they govern. If this file ever conflicts with `CLAUDE.md`'s
resident roles/prohibitions, `CLAUDE.md` wins.

Use `/opsx:apply` to implement tasks from an active change.

## The DEVLOG — the change's working record

Every active change keeps a **`DEVLOG.md`** next to its `tasks.md` (`openspec/changes/<slug>/DEVLOG.md`). **You (the Architect) own it** — the agents report back to you and you record; they don't write to it themselves. Conventions:

- Organised by `## N.` **section** (mirroring `tasks.md`), with a pinned `## NEXT` at the bottom.
- **The first post under each `## N.` heading is the section's base commit** — `**[architect]** Base: <sha> — <what this section delivers>`. The supervisor's review scope is `git diff <sha>..HEAD`, so this post is load-bearing, not ceremony.
- Posts are **attributed** to whose work they record — `[architect]`, `[worker]`, `[reviewer]`, `[supervisor]` — and reference the **block** (`N.1`–`N.3`) they concern.
- **Append-only** — posts persist; only `## NEXT` is rewritten. It is committed with each block and moves to the archive with the change, so a shipped change's DEVLOG is the durable record of *how* it was built.

Maintain it via the devlog skill.

## Pre-flight (Architect, before the first block)

1. Read `proposal.md`, `design.md` (especially **`## Decisions`** and **`## Open Questions`**), and the relevant `specs/<cap>/spec.md` for the section(s) you're about to work.
2. **Working tree must be clean** (`git status`). If dirty, stop and ask.
3. **Change must validate:** `openspec validate <slug> --strict`. If not, stop and ask.
4. **Be on the change branch** `change/<slug>`. Create it from `main` if missing: `git switch -c change/<slug>`.
5. **Check the preceding section closed.** Ticked boxes are not proof a section passed its supervisor review — a session can end after the last block commits and before the review runs. Before starting the resume point's section, read the DEVLOG: if the previous `## N.` has no `[supervisor]` `Approve` under it, run that review first (3c). If it never got a `Base:` post either, reconstruct the range from `git log` and say so in the DEVLOG.

## Implement — section by section, block by block

Walk the change's `## N.` sections in order from the resume point. There are **two nested loops**:

```
OUTER — for each ## N. section, in order
  ├─ post the section's base commit to the DEVLOG
  ├─ INNER — for each block in the section
  │    brief worker → worker implements → reviewer audits → loop until Approve
  │    → gates pass → tick boxes → commit
  └─ SECTION REVIEW — supervisor audits the whole section
       Approve → next section
       Request changes → carve a remediation block, re-enter INNER
```

**The unit of work is not the whole section — it is a *block*:** the **smallest reasonable, independently gate-passing** slice of remaining tasks — one task (e.g. `1.3`) or a small contiguous range (e.g. `1.3`–`1.5`). You carve each section into blocks; a section is one or more blocks, and **a block never spans sections** — if a block wants to, the section breakdown is wrong.

### 3a. Opening a section (outer loop)

Before briefing the first block of a `## N.` section, post its **base commit** to the DEVLOG as the first entry under that heading:

```
**[architect]** Base: <sha> — <one line: what this section delivers>
```

`<sha>` is the current `HEAD` (`git rev-parse --short HEAD`). This is what gives the supervisor its review scope at the end of the section (`git diff <sha>..HEAD`); without it, it has no reliable way to see the section as a whole. Post it **before** any block of the section is committed.

### 3b. Each block (inner loop)

**Carving the block.** From the remaining unticked tasks in this section, choose the smallest contiguous run that is a coherent, independently shippable deliverable. Heuristics:

- **Independently green-able.** After the worker finishes, every gate must pass. A block that leaves a dangling reference, an unimplemented interface member, or a red test is too small or wrongly cut. (Watch for cross-project breaks: adding a member to an interface that test fakes implement means the fake stub belongs in the *same* block.)
- **Coherent deliverable.** The block should map to a sentence: "read the informational version in `RpcHostedService`", "wire the provider factory". If you can't name it cleanly, the cut is wrong.
- **Respect dependencies.** A type or RPC contract before the behaviour that uses it; a fake/seam before the test that drives it. Read `tasks.md` order and `DEVLOG.md` for the real sequence; the lowest-numbered unticked task is the usual — but not automatic — starting point.
- **Contract-, permission-, persistence-, or load-touching tasks deserve their own block**, with an explicit call-out in the brief that the reviewer will hammer them: the wire shape (ADR-003 JSONL/stdio Pi-shape, ADR-015 typed correlated results), the permission model (ADR-006), session storage append-only semantics (ADR-004), extension loading (ADR-008), and "no third-party types in the API" (ADR-016).
- **Don't over-bundle.** When in doubt, cut smaller — a tight block reviews faster and commits cleaner.
- **Size to the worker's context window.** The `worker` runs on **Sonnet** — a smaller context window than yours. Scope each block so the worker's whole job (your brief + the files it must read + the code and tests it writes + running the gates) comfortably fits, **aiming to stay under ~100k tokens**. If a block would force the worker to load many large files or sprawl across many projects to do it well, that's a signal to cut it smaller or split it. A brief that sends the worker spelunking blows this budget — keep briefs self-contained and point at *specific* files/symbols, not whole directories. Prefer `graphify query "<question>"` over raw grep when locating code to point at.

Then run the block:

1. **Brief the worker.** Post the brief to the DEVLOG (`[architect]`, under the block's `## N.` section) and hand it to the `worker`. It must be **self-contained** — the worker should not have to go hunting:
   - **Block:** the change slug + exact task ids + a one-line name of the deliverable.
   - **Tasks:** the verbatim task text for each id.
   - **Binding design decisions / ADRs:** the `design.md` decision ids and the ADR clauses that bind this block, each with a one-line gloss; plus any already-resolved decision from `DEVLOG.md`. Quote the specific clauses the block touches; don't dump the whole list.
   - **Spec excerpts that bind this block:** quoted requirement/scenario text from `specs/<cap>/spec.md`.
   - **Scope boundaries:** what this block does NOT do, and which later block owns the deferred parts.
   - **Investigate first:** the specific files/symbols to read before writing, and why.
   - **Contract / permission / persistence / load hazards:** the invariants the reviewer will check hardest.
   - **Gates:** the list below.

   The worker implements the **whole block** — splittable across multiple `worker` calls if needed, but it remains **one commit** at block end.
2. **Worker implements the block** and reports back.
3. **Audit.** Spawn `reviewer` on the **block diff** (correctness, ADR compliance, OpenSpec scope, C# idiom, agentic-AI design quality, security).
4. **Review loop.** Feed the reviewer's findings to the `worker`; worker fixes; `reviewer` re-audits. **Repeat until the reviewer signs off.** (Doc-only spec/design realignments and the `DEVLOG.md` are yours to edit — agents don't.)
5. **Gates — all must pass before ticking any box:**
   - `make build` clean (no errors; `TreatWarningsAsErrors` clean)
   - `make test` (or `env -u MEKO_API_KEY make test` to avoid the live-Meko smoke hang) green — new tests for the block **and** all existing tests
   - `openspec validate <slug> --strict`

   If a gate fails, it's back to step 4, not a commit.
6. **Tick the boxes.** Mark every `- [x] N.M` in the block in `tasks.md`. Never rewrite `tasks.md` wholesale — only flip `[ ]→[x]`.
7. **Update the DEVLOG.** Record the block's decisions/deviations, the reviewer's verdict, and anything a later block needs to know.
8. **Commit — one conventional commit per block**, scoped to the component, with the change slug in the body (`Change: <slug>`). Use the real task ids the block covered. Commit the DEVLOG with the block. Then loop back to step 1 for the next block in this section.

### 3c. Closing a section — the supervisor review

When the **last block of a `## N.` section** has landed (reviewer approved, gates green, boxes ticked, committed), the section is not done yet. Run the section review before opening the next one.

1. **Spawn `supervisor`** on the section's full range — `git diff <base-sha>..HEAD`, where `<base-sha>` is the one you posted in 3a. Point it at the section's spec requirements, not just its tasks. Record its verdict in the DEVLOG under the section's heading as `[supervisor]`.
   - Run it for **every** section, including a single-block one — the lens is different from the reviewer's, not merely wider.
2. **`Approve`** → the section is closed. Roll any architectural notes into `## NEXT` and move to the next section.
3. **`Request changes`** → carve a **remediation block** from the findings and re-enter the inner loop (3b) with it: brief the worker, `reviewer` audits it, gates, commit.
   - The remediation block gets **no new `N.M` numbers** and ticks nothing — every box in the section is already ticked. The findings and the fix live in the DEVLOG; that is the record.
   - Commit it as a fix, not a feature: `fix(<component>): address supervisor findings (section N)`, with the findings and what changed in the body plus `Change: <slug>`.
   - Then **re-run the supervisor** on the same `<base-sha>..HEAD` range (now including the fix).
4. **Two rounds, then stop.** If the supervisor still requests changes after one remediation block, **do not carve a third** — stop and put it to the Product Owner. A section that won't converge in two rounds usually means the section breakdown or the spec is wrong, and more fixing won't resolve either.

**Do not open the next section until the current one has a supervisor `Approve`** (or the Product Owner has explicitly waved it on). The whole point of the outer loop is that drift is caught before it is built on.

## Done

When every task is ticked **and the final section has a supervisor `Approve`**: report sections closed, blocks completed, commits made, the test summary, and any architectural notes the supervisor parked in `## NEXT`; push the `change/<slug>` branch (and open a PR) when the user asks; then **propose `/opsx:archive`** and **wait for confirmation**. Do not archive automatically.
