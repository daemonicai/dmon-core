# .NET Rocks Talking Points — dmon

> **Format:** guest · **Arc:** why I built it → how it works → ecosystem take

---

## 1. The hook (first 2 minutes)

- Coding agents are everywhere, but they all feel like they were built *around* a language model, not for the developer.
- I use these tools every day and I kept hitting the same walls: they're TypeScript-shaped, or they assume you're living in VS Code, or they're opaque black boxes you can't extend without forking them.
- So I started building one in .NET — not a port of anything, a ground-up .NET-native agent — and the process taught me a lot about where our ecosystem is and where it's going.

---

## 2. Why I built it (personal story)

### The catalyst: Pi
- Pi (`earendil-works/pi`) is a coding agent written in TypeScript/Node. It has one killer idea: the agent is *self-extensible* — you ask it to write a tool for itself, type `/reload`, and it just works mid-session.
- I love that idea. I wanted it in .NET. But I didn't want to port TypeScript to C# — that's just moving the same shapes around.
- "Inspired by, not a port" — keep what makes Pi good, drop what's TypeScript-shaped, lean into things .NET does well.

### What was I actually missing?
- A coding agent that feels native to C# developers — not a thin wrapper around a Python SDK.
- A first-class extension model that isn't "edit a JSON config and restart."
- A clear separation between the agent brain and whatever UI you put in front of it.

---

## 3. What dmon actually is

- **Name:** dmon — pronounced "demon." Short for daemon, which is what it is: a background process you talk to.
- **Stack:** C# 13 / .NET 10, `Microsoft.Extensions.AI` for LLM abstraction, JSONL over stdio for the RPC surface.
- **Structure:** Agent core is a separate process. Hosts (console/TUI today, Avalonia desktop later) are thin frontends over that RPC. The agent doesn't know or care what's rendering it.
- **Not a library, not a plugin:** it's a process you run, and anything can talk to it.

---

## 4. How it works — architecture

### Process isolation (the first good decision)
- The agent core lives in its own process and speaks JSONL over stdio.
- The console host and (eventual) Avalonia desktop host are both thin RPC clients — they share zero UI code but share all agent behaviour.
- This also means: third-party frontends (Emacs, a web app, a VS Code extension) can point at the same agent core without touching it.
- Compare to the "shared library" model — simpler on day one, boxes you in fast.

### `Microsoft.Extensions.AI` / `IChatClient`
- M.E.AI gives us a provider-agnostic `IChatClient`. Swap Anthropic for OpenAI for Azure for Ollama by changing config — no code changes.
- It's relatively new and underappreciated in the .NET ecosystem. It's the right abstraction layer for this problem.
- Tools/functions are `AIFunction` — the same type whether you're running a built-in file-reader or a user-written extension.

### Session = a directory on disk
- Each conversation is a relocatable directory: `messages.jsonl` (append-only), `meta.json`, an `attachments/` folder for large outputs.
- Because it's just files, you can `cp`, share, fork, or inspect a session without any special tooling.
- Inspired directly by Pi. It's the right model.

### Permission model
- Conservative by default. Reading files inside your working directory is implicit. Writes, bash commands, network calls — all prompt.
- Tiered: read / write / bash / network. The agent knows what tier a tool needs before calling it.

---

## 5. The extension model ← THE THING

This is the part I'm most excited about. Two tiers, with a promotion path between them.

### Tier 1: `.csx` scripts (hot-loaded)
- Roslyn scripting API (`Microsoft.CodeAnalysis.CSharp.Scripting`) lets us compile and run `.csx` files at runtime.
- You ask the agent to build you a tool — say, a function that queries your local database — it writes a `.csx` file, you type `/reload`, and the tool is live. Mid-session. No restart.
- This is the Pi magic, rebuilt natively in .NET.

### Tier 2: NuGet packages
- For serious extensions — ones with multiple files, external dependencies, types you want to share — you build a NuGet package.
- Loaded via `AssemblyLoadContext` so they're isolated and can be unloaded.
- Consume the same `AIFunction`/`IDaemonExtension` surface as the built-ins.

### The promotion path (the novel bit)
- There's a `promote` command that takes a stable `.csx` script and scaffolds it into a proper `.csproj`.
- The script body becomes the entry point. You get a real project with tests, versioning, the works.
- The journey: *agent writes it* → *you iterate on it* → *it becomes a package*.
- This is the idea I haven't seen elsewhere: the agent is the first-draft author of its own extensions.

---

## 6. Distribution profiles

- **JIT self-contained (~70–100 MB):** full runtime extensibility. Roslyn and `AssemblyLoadContext` both available.
- **AOT-trimmed (~15–30 MB):** built-in tools only, no runtime extension loading. For people who want one small CLI binary with no surprises.
- Both from the same codebase — the extension loading code is behind a compile-time flag.
- Haven't decided which audience matters more yet. Building both, watching.

---

## 7. The .NET ecosystem take

- **The good:** `Microsoft.Extensions.AI` is excellent. .NET 10 AOT/trimming story is genuinely compelling. `AssemblyLoadContext` makes dynamic loading tractable.
- **The gap:** The AI agent/LLM tooling ecosystem is Python-first. Most SDKs, most tutorials, most examples. .NET developers are underserved.
- **Why that's an opportunity:** .NET runs a huge fraction of enterprise software. Those teams need coding agents too — and they need agents that understand C#, MSBuild, NuGet, and the .NET runtime. Python-wrapped tools don't give you that.
- **The Roslyn angle:** No other runtime has Roslyn. The ability to parse, analyse, and compile C# *at runtime* — from within a C# process — is genuinely unique. dmon leans into this.

---

## 8. Where it's going

**V1 (in progress):**
- Console/TUI host, stable RPC surface, core extension model working end-to-end.
- Multi-provider switching. Session management (fork, resume, share).
- The `.csx` → NuGet promotion flow.

**V1.5+ (planned):**
- Avalonia desktop host — visual diffs before approving edits, side-by-side panels, session graph for forked conversations.
- Extension browser/marketplace.

**Deliberately out of scope for now:**
- Multi-agent orchestration (interesting, not what makes a single-agent tool good).
- OAuth (API keys are fine for V1).
- Mobile.

---

## 9. Things to have ready as answers

| Likely question | Short answer |
|---|---|
| "Why not just use Claude Code / Copilot?" | They're great tools. dmon is for developers who want to *own* their agent — extend it, fork it, run it on-prem, build on top of it. |
| "Is this open source?" | *(check your plans and fill this in)* |
| "How does it compare to Semantic Kernel?" | SK is an orchestration framework — you build apps on top of it. dmon is a finished agent you run. Different layer. SK could theoretically host inside dmon as an extension. |
| "What's M.E.AI vs SK?" | M.E.AI is the thin abstraction (`IChatClient`). SK is a framework built on top of M.E.AI. We use M.E.AI directly — lighter, more control. |
| "Can I use Ollama / local models?" | Yes — any `IChatClient` provider works, including Ollama. |
| "What's the biggest challenge so far?" | Getting the permission model right. Conservative enough to be safe, permissive enough not to be annoying. It's a UX problem as much as a security one. |

---

## 10. One-line soundbites (for when you need to be quotable)

- *"The agent is the first-draft author of its own extensions."*
- *"Session-as-a-directory — you can `cp` a conversation."*
- *"Not a port of Pi. Inspired by Pi. We kept the ideas, rewrote the idioms."*
- *"Every .NET enterprise shop needs a coding agent that actually understands .NET."*
- *"Microsoft.Extensions.AI is the right abstraction and not enough people know it exists yet."*

---

*Good luck! Remember: you can always say "let's come back to that" and park anything you're not ready to answer live.*
