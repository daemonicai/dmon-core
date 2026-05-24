# Avalonia Desktop Host

**Status:** 💭 Idea  
**Depends on:** Stable RPC surface (V1), console host proven in use

---

## What

A desktop UI host for dmon built with [Avalonia](https://avaloniaui.net). Like the console host, it's a thin RPC client over the agent core — same agent, different surface.

## Why

A terminal can't do everything well. Some affordances are genuinely better as GUI:

- Reviewing and approving file edits is much nicer with a visual diff than a text patch.
- Side-by-side panels (file tree, tool output, web preview) give context without toggling.
- Forked sessions have a natural graph structure — worth visualising.
- Extension browsing and management is friendlier with a UI.

## Ideas for what it includes

- **Visual diff viewer** — before approving any file write, see a syntax-highlighted diff with accept/reject/edit inline.
- **Side-by-side panels** — file tree, terminal output, and a web preview pane, arranged around the conversation.
- **Session graph** — forked conversations rendered as a branching tree; click any node to resume from that point.
- **Multi-session tabs** — run multiple agent sessions at once, each in its own tab.
- **Extension browser** — browse, install, enable/disable extensions without touching a config file.

## Notes

- Must not share any UI code with the console host. Both are clients of the same RPC surface.
- Avalonia runs on Windows, macOS, Linux — same binary for all three.
- Don't build this before the RPC surface is stable. The console host is the proving ground.
- The Avalonia host is V1.5+ — explicitly out of scope for V1.
