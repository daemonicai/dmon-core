# `SpySessionStore`-based turn tests cannot see persistence

**Status:** open
**Where:** `test/Dmon.Core.Tests/Rpc/TurnHandlerIntegrationTests.cs`
**Surfaced:** 2026-09-09, block 3B of `lazy-session-creation` — reviewer, endorsed by the section-3 supervisor
**Severity:** medium — the tests pass for a reason weaker than they appear to

## What

`TurnHandlerIntegrationTests` pairs an `ISessionHandler` fake that mints a
`SessionMeta` in memory (calling **no** `ISessionStore` at all) with a
`SpySessionStore` that records `AppendMessagesAsync` calls and writes nothing to
disk.

In production the store that *creates* a session and the store that *appends* to
it are the same object. In these fakes they are two unrelated objects that never
meet. So the tests cannot detect that no session directory was created, that
`messages.jsonl` is absent, that the id handed to `AppendMessagesAsync` is not
the id the store minted, or that `agent` was mis-bound.

## Why it matters

The failure mode is specific and it nearly bit `lazy-session-creation`. A
fake-based persistence test *would* have failed against the pre-change code
(null `CurrentSession` → no append), so it appears to satisfy a "must fail
against the pre-change code" requirement — while proving nothing whatsoever about
persistence. That trap was caught by the section-2 supervisor, which is why tasks
`3.4`–`3.7` were required to run against a real `SessionHandler` over a real
`SessionStore`.

The fakes remain adequate for what they were kept for in that change — ordering,
event emission, and the loud persist guard — but any *future* task that reaches
for this harness to assert something about persistence inherits the trap.

## What to do

Supplement (not necessarily replace) the fake-store tests with the `FakeResolver`
pattern introduced in `test/Dmon.Core.Tests/Rpc/LazySessionCreationRealStackTests.cs`:
a real `SessionStore` and real `AttachmentStore` on a real filesystem, isolated
only by redirecting `ISessionDirectoryResolver.Resolve()` to a temp path. That
seam is pure path computation — `SessionStore.GetRoot()` still performs the real
`Directory.CreateDirectory` — and, decisively, the creating and appending paths
share one resolver instance.

Convert the cases that assert *outcomes* (what was persisted, where, under which
id). Leave the ones that assert *interactions* (call counts, ordering, emitted
events), where a spy is the right tool.

Verified: the divergence was confirmed by reading both harnesses; the assertion
that it is *currently* harmless in that file rests on those tests not claiming
anything about persistence today.
