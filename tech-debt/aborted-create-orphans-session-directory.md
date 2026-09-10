# An aborted `session.create` orphans a `meta.json`-less directory

**Status:** open
**Where:** `core/Dmon.Core/Session/SessionStore.cs:98-114`
**Surfaced:** 2026-09-09, section-3 supervisor of `lazy-session-creation`
**Severity:** low — **ruled out** (2026-09-10) as the explanation for the empty-session litter; see [Checked](#checked-2026-09-10)

## What

`SessionStore.CreateAsync` builds the whole directory tree **before** its first
`await`:

```
Directory.CreateDirectory(sessionDir);                       // :98
Directory.CreateDirectory(Path.Combine(sessionDir, "attachments"));
File.Create(Path.Combine(sessionDir, "messages.jsonl")).Dispose();
…
await WriteMetaAsync(sessionDir, meta, cancellationToken);   // :114  ← first await
```

A cancellation or throw at `:114` leaves a directory containing an empty
`messages.jsonl` and an `attachments/` folder, with **no `meta.json`** and
nothing tracking it. `_currentSession` stays null, so the next turn simply
creates a fresh session.

## Why it matters

Not host divergence — the host is never misinformed, and this is shared with the
explicit `session.create` path, so it is not new. What *is* new is reachability:
before `lazy-session-creation`, reaching `CreateAsync` required a user to type
`/new`. Now a first turn reaches it, so an abort during the create window is
something an ordinary user can hit without any session-related action.

**The reason this note may matter more than its severity suggests:** the change
that surfaced it recorded that **764 of 769** session directories under this
repo's `.dmon/sessions` have an empty `messages.jsonl` (99.3%), and 426 of 787
under `~/.dmon/sessions`. That evidence is what decided design D1 (lazy, not
eager creation), and `proposal.md` explicitly parked *pruning* the litter for its
own change — noting that such a change "should begin by establishing **what**
creates those directories". This is a candidate mechanism. It is **not**
established as the cause: the test suite writing into the repo's `.dmon` is at
least as plausible, and nobody has checked whether the litter directories lack
`meta.json` (which would implicate this path) or have one (which would not).

**That check is cheap and should come first.** It is the difference between a
tidy-up and a fix.

## Checked (2026-09-10)

The check was run, and the answer is **tidy-up, not fix**. Verified by counting
every directory, not by sampling:

| Store | Dirs | Empty `messages.jsonl` | …of which lack `meta.json` |
|---|---|---|---|
| repo `.dmon/sessions` | 770 | 764 | **1** |
| `~/.dmon/sessions` | 845 | 455 | **0** |

This path produced exactly one orphan
(`19831aab-d6fc-4092-8f2b-8478914821cd`, 2026-09-07), so it is real but rare.
The litter has other sources:

- **`~/.dmon/sessions`: every session with content is test output.** See
  [the live e2e test writes into the home session store](live-e2e-test-writes-into-home-session-store.md).
  Its *empty* sessions are mixed: most are that test's twins, but real hosts whose
  working directory has no `.dmon/config.yaml` (such as dmon-home's gateway core) also
  write there.
- **The repo's litter is historical.** 744 of its 764 empty sessions were created
  between 2026-05-25 and 2026-06-14, 598 of them in a burst from 06-11 to 06-13. No
  empty session has appeared since then, apart from the one orphan above. The
  source was not identified. It has stopped, so it only matters for pruning.

## What to do

1. ~~Establish the fact.~~ Done. See above.
2. Determine how `ListAsync` behaves on a `meta.json`-less directory — whether it
   skips, throws, or yields a half-populated `SessionMeta`. Unknown at the time
   of writing, and it governs whether the orphan is inert or actively harmful.
3. If worth fixing, make creation atomic-ish: build into a temp directory and
   move into place after `meta.json` is written, or write `meta.json` first.

Feeds the parked litter-pruning change referenced in
`openspec/changes/archive/*lazy-session-creation*/proposal.md`.
