# A Desktop reload can permanently kill the session's event stream

**Status:** open
**Where:** `frontends/Dmon.Desktop/CoreSessionService.cs` — `PumpEventsAsync` / `CompleteSubjects` / `ReloadAsync`
**Surfaced:** 2026-09-09, section-7 supervisor of `lazy-session-creation` (note A2)
**Severity:** medium — silent, unrecoverable for the window's lifetime, and newly reachable

## What

`PumpEventsAsync` consumes the core's events with an `await foreach` and calls
`CompleteSubjects()` when that loop exits **normally**. `CompleteSubjects()`
calls `_eventSubject.OnCompleted()`, and `_eventSubject` is **never recreated**.

`ReloadAsync` cancels `_sessionCts` before disposing the client, so the loop
normally exits via `OperationCanceledException` and the subject survives. But if
disposal completes the underlying channel *first*, the loop exits normally
instead, the subject is completed, and every `SessionViewModel` subscription is
dead for the remaining life of the window.

The failure is silent and looks like success: the reload completes, the
`session.load` is still sent, and the UI simply stops receiving events.

## Why it matters

Pre-existing — it belongs to the Avalonia desktop-host change, not to
`lazy-session-creation` — and the cancel-before-dispose ordering makes it
unlikely. What changed is that **users now have a reason to reload.**

Before `lazy-session-creation`, Desktop's reload re-attach was unreachable
(`_activeSessionId` was never set, because Desktop sends none of the commands
that used to set it), so reload could not preserve a conversation and there was
little reason to press it. Section 7 fixed that conformance defect: reload now
re-opens the active session and re-seeds turn history. A path that was rarely
exercised is now the recommended way to pick up a config change mid-conversation.

Race-shaped and unobserved in the wild. Recorded as **inferred from the code**,
not reproduced — the ordering argument above is the whole of the evidence.

## What to do

Make the subject's lifetime survive a reload, rather than relying on which of
two racing completions wins:

- distinguish "the pump ended because we are reloading" from "the core's stream
  ended for good" — e.g. only `CompleteSubjects()` when the service itself is
  being disposed, not on every normal loop exit; or
- recreate `_eventSubject` as part of `ReloadAsync`'s rebind, so a completed
  subject cannot outlive the reload.

A regression test is awkward (it needs the losing side of the race) but the first
option is testable directly: assert that after `ReloadAsync`, a pushed event
still reaches an existing subscriber.

Related: section 7 of `lazy-session-creation` and the `desktop-host` standing
spec's `/reload` requirement.
