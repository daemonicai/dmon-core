# ADR-026: Memory as a Top-Level Bucket, Not Middleware

**Date:** 2026-06-16
**Status:** Accepted
**Amends:** ADR-025 (Decision 2 — bucket set; Decision 5/6 — the `memory` openspec/ADR placement; Decision 11 — the `dmon-meko` landing target)

## Context

ADR-025 Decision 2 lays out the monorepo's top-level buckets "by ADR-023 role" and files memory under `middleware/`:

> `middleware/` — `Memory`, `Memory.Meko`.

and Decision 11 sets the satellite's landing target accordingly: `dmon-meko → middleware/memory-meko`. Phase 0 (`monorepo-phase0-reorg`) duly placed the short-term tier at `middleware/Dmon.Memory`.

The premise — that memory mirrors an ADR-023 *role* — does not hold on inspection. ADR-023's middleware role is concrete: an `IDmonMiddleware` / `IMiddlewareRegistration` participant in the chat pipeline (a delegating `IChatClient` handler, `core/Dmon.Abstractions/Extensions/IDmonMiddleware.cs`, registered via `IDmonHostBuilder`). **Memory implements none of that.** `AddDmonMemory()` registers plain DI services; `IMemory` is consumed by `SessionStore` in the *core* (resolved via `Lazy<IMemory?>` to break a DI cycle), never as a pipeline stage. `Dmon.Memory` landed in `middleware/` as a filing convenience, not because it is middleware.

Structurally, memory is a **contract-set + N backends + facade** family:

| Bucket | Shape |
|--------|-------|
| `providers/` | one contract (`IChatClient` factory) + N backends (Anthropic, OpenAI, LlamaCpp, …) |
| `tools/` | one contract (`IToolExtension`) + N tools (Builtin, Dmail, …) |
| **memory** | one contract-set (`IMemory` / `IShortTermMemory` / `ILongTermMemory`, in `core/Dmon.Abstractions.Memory`) + N backends (sqlite-vec local short-term, Meko hosted long-term, future) + the `IMemory` facade |
| `middleware/` | `IDmonMiddleware` pipeline interceptors |

Memory has the same "contract + N pluggable backends" shape as `providers/` and `tools/`, and a shape unlike `middleware/`. Filing it under `middleware/` both mis-describes memory and dilutes what `middleware/` means — the bucket stops denoting the ADR-023 role and becomes "the drawer where memory happens to live." With memory removed, `middleware/` has **no members at all**: there is no `IDmonMiddleware` implementation anywhere in the tree yet.

## Decision

1. **Memory is its own top-level bucket: `memory/`.** It joins `core/ providers/ tools/ middleware/ frontends/` (+ `samples/`, `libs/`) as a peer bucket. This **amends ADR-025 Decision 2**: memory is no longer listed under `middleware/`.

2. **The bucket holds the backend implementations + facade, keyed to the memory contract-set.**
   - `memory/Dmon.Memory` — the local short-term tier (sqlite-vec + FTS5 + local embeddings) and the `IMemory` facade.
   - `memory/Dmon.Memory.Meko` — the durable long-term tier over Meko (the grafted `dmon-meko`).
   - Future backends (`memory/Dmon.Memory.<Name>`) land here under the same naming family.

3. **The contracts stay in `core/`.** `Dmon.Abstractions.Memory` (`IMemory`, `IMemoryStore`, `IShortTermMemory`, `ILongTermMemory`, DTOs) remains part of the `core/` contract surface, consistent with ADR-025 Decision 2's placement of `Dmon.Abstractions` in `core/`. Only the *implementations* move to `memory/`.

4. **`middleware/` is retained as a defined ADR-023 role with no current members.** The bucket directory is created when the first real `IDmonMiddleware` implementation ships, not before. Keeping the role named (rather than deleting it) preserves ADR-023's taxonomy and ADR-024's release-family wording.

5. **`dmon-meko` grafts to `memory/Dmon.Memory.Meko`.** This **amends ADR-025 Decision 11** (`dmon-meko → middleware/memory-meko`). No type rename is implied (`Dmon.Memory.Meko` keeps its name; the earlier informal "→ `Dmon.Middleware.Meko`" idea is dropped). The import mechanics (filter-repo + `--allow-unrelated-histories`, stabilise-on-`main`-first, archive source) are unchanged from ADR-025 Decision 13.

6. **The `memory` capability resolves under the hybrid-openspec boundary rule with no rename.** This **amends ADR-025 Decisions 5/6** only in the path: the engine-side memory abstraction is a root/`core` concern; the Meko backend's component openspec lives under `memory/Dmon.Memory.Meko/openspec` (was `middleware/memory-meko/openspec`). The boundary rule itself is unchanged.

## Consequences

- **`memory/` describes itself.** A contract-plus-backends family sits in a bucket named for it, parallel to `providers/` and `tools/` — the mental model is uniform across pluggable-backend subsystems.
- **`middleware/` recovers its meaning.** It denotes exactly the ADR-023 chat-pipeline role; its first physical member will be an actual `IDmonMiddleware`. Until then the bucket is a named-but-empty role, which is honest about the current tree.
- **One in-tree move.** `middleware/Dmon.Memory` → `memory/Dmon.Memory` is a history-preserving `git mv` plus `.slnx` / `Everything.slnx` / Makefile repath; the `middleware.slnx` (whose only member was Memory) is dropped or emptied. The cost is small and contained to the forthcoming graft change's first group.
- **The release matrix wording widens by one family name.** ADR-025 Decision 10's NuGet family "core + providers + tools + middleware + Terminal tool" reads as "… + memory + …"; the per-package trigger model (ADR-024) is unchanged.
- **No code-contract change.** Interfaces, DI verbs (`AddDmonMemory`, `AddMekoLongTermMemory`), and the facade are untouched — this is a placement/taxonomy decision, not an API one.

## Alternatives

- **Keep memory in `middleware/` (ADR-025 as written).** Rejected: perpetuates the category error, leaves `middleware/` meaning two things, and the in-tree move only gets more expensive as the long-term tier and future backends accrete. Cheapest to fix now, before the `dmon-meko` graft lands a second package in the wrong bucket.
- **Make memory *actually* middleware** (wrap recall as an `IDmonMiddleware` that injects context each turn). Rejected as the *placement* rationale: even if such a middleware is later built (it may well be — see below), the *stores* remain DI services with the contract-plus-backends shape; the middleware would consume `IMemory`, not be it. Surfacing memory through a pipeline stage is an orthogonal future feature, not a reason to file the backends under `middleware/`.
- **Delete the `middleware/` bucket entirely** (no members today). Rejected: ADR-023's role taxonomy and ADR-024's release-family wording both reference middleware; retaining the named role with no members costs nothing and avoids re-amending those ADRs when the first middleware ships.

## Open Questions

- **A. Recall-injection middleware.** If/when memory is surfaced as an `IDmonMiddleware` (auto-injecting recalled context into each turn) — or as agent-callable memory tools — that participant lands in `middleware/` (or `tools/`) and *consumes* `memory/`. Out of scope here; flagged so the boundary is clear when it arrives.
- **B. dmon-meko's pending refactor.** `dmon-meko`'s `main` carries an unstarted change `add-memory-abstraction` (0/36). Whether it lands before the graft or as its own change inside the monorepo afterward is decided in the graft change's `design.md`; the default is to graft current `main` as-is.

## Relationship to other ADRs

- **ADR-025** — amended in Decisions 2, 5/6, and 11 (see header). Decision 13's import mechanics, Decision 4's intra-repo `ProjectReference` rule, and the per-area `.slnx`/`Everything.slnx` topology all carry over unchanged to the new bucket.
- **ADR-023** — the role taxonomy is honoured, not broken: memory was never one of its three roles (provider/tool/middleware); this ADR stops forcing it into one. The `Dmon.Memory.<Name>` naming follows ADR-023's granular-package families.
- **ADR-024** — versioning and the per-package release trigger are unchanged; `memory/` packages version on the protocol cycle like every other first-party package.
- **ADR-016** — session-storage's lossless parts record remains the source the memory tiers index from; unaffected by where the tier implementations live.
