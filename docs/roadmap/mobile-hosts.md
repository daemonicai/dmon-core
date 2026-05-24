# Mobile Hosts

**Status:** 💭 Idea  
**Depends on:** Remote execution, Avalonia host, clear mobile use case

---

## What

Host surfaces for dmon on mobile platforms — iOS and/or Android. Like the desktop and console hosts, a thin RPC client over the agent core.

## Why

If the agent core can run remotely (see [remote-execution.md](remote-execution.md)), a mobile host is "just" a frontend. The question is whether there's a compelling use case for interacting with a coding agent from a phone or tablet.

## Possible use cases

- Reviewing and approving agent-proposed changes from a phone while away from a desk.
- Lightweight "ask the agent" queries against a remote session.
- Tablet as a secondary screen for the session graph / diff view alongside a desktop.

## Notes

- This is the most speculative item on the roadmap. Don't build until there's a clear user need.
- Avalonia has experimental mobile support — worth watching but not stable enough to depend on.
- Requires remote execution to be working first — running the agent core on a phone is not a goal.
- Explicitly out of scope for V1 and V1.5.
