# Observer single-construction is convention, not construction

**Status:** open — two instances; a third is the trigger to close it structurally
**Where:** `home/App/DmonHomeApp/ChildStatusObserver.swift`, `home/App/DmonHomeApp/ChildLogObserver.swift`
**Surfaced:** 2026-08-06, by the section-5 supervisor of `dmon-home-foundations`
**Severity:** medium — the leak it guards against is real and already happened once

## What

Both observers carry a doc comment saying "construct exactly one per
`HostRuntime`, and never let a view construct its own". That rule guards a
**real** leak that was already shipped and fixed once: `@State` only *uses* its
first-constructed value for a given view identity, but the initializer
expression still runs on every `ContentView.init` — and closing the window and
reopening it from the Dock constructs a fresh `ContentView` with a new identity.
Every reopen leaked one more subscriber into `HostRuntime`, permanently.

The fix was ownership: `AppDelegate` — itself guaranteed singular by
`NSApplicationDelegateAdaptor` — constructs the one observer and hands it down.

## Why it matters

**The app target has no test bundle at all.** So a future view that constructs
its own observer reintroduces the leak with no compiler and no test signal —
only the doc comment stands between the codebase and a repeat.

That was tolerable at one instance. At two it is a pattern, and the pattern's
enforcement mechanism is prose.

## What to do

Not yet. The section-5 supervisor's ruling, which is the right one: **a third
observer makes it worth closing by construction.** Until then the cost of a
structural guard (a private initializer plus a factory the app target owns, or
folding the mirrors into one type) exceeds the risk.

When it is worth doing, prefer making the wrong call *unrepresentable* over
adding an app-target test bundle — design D4 keeps the app target a shell
deliberately, and a test bundle there costs a second scheme plus a CI change.

## Related

The same "guarded by comment, not by construction" shape appears in
[the shutdown walk](shutdown-walk-is-cancellable.md). Worth noticing if a third
instance of *that* shape shows up too — at that point the project has a habit,
not three coincidences.
