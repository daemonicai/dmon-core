# An aborted `session.create` orphans a `meta.json`-less directory

**Status:** open
**Where:** `core/Dmon.Core/Session/SessionStore.cs:98-114`
**Surfaced:** 2026-09-09, section-3 supervisor of `lazy-session-creation`
**Severity:** low for correctness, **possibly high as the explanation for a measured problem**

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

## What to do

1. Establish the fact: sample the empty-`messages.jsonl` directories and see how
   many lack `meta.json`. That decides whether this is the mechanism.
2. Determine how `ListAsync` behaves on a `meta.json`-less directory — whether it
   skips, throws, or yields a half-populated `SessionMeta`. Unknown at the time
   of writing, and it governs whether the orphan is inert or actively harmful.
3. If worth fixing, make creation atomic-ish: build into a temp directory and
   move into place after `meta.json` is written, or write `meta.json` first.

Feeds the parked litter-pruning change referenced in
`openspec/changes/archive/*lazy-session-creation*/proposal.md`.
