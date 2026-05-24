# AOT-Trimmed Distribution Profile

**Status:** 💭 Idea  
**Depends on:** Core feature set stable

---

## What

A Native AOT build of dmon that produces a small (~15–30 MB), self-contained single binary with no runtime extension loading. For users who want a coding agent they can just drop on a machine and run.

## Why

Not everyone wants or needs the full extension model. Some users — especially in CI/CD, containers, or restricted environments — want the smallest possible binary with no external dependencies and predictable startup time. AOT gives us that.

## Tradeoffs

| | JIT self-contained | AOT-trimmed |
|---|---|---|
| Size | ~70–100 MB | ~15–30 MB |
| Startup | ~100–300 ms | <10 ms |
| `.csx` extensions | ✅ Yes (Roslyn) | ❌ No |
| NuGet extensions | ✅ Yes (ALC) | ❌ No |
| Built-in tools | ✅ | ✅ |
| Trimming-safe code required | No | Yes |

## Ideas for what it includes

- **Compile-time flag** — `DMON_AOT=true` (or a separate project/profile) that disables all dynamic loading code paths.
- **Built-in tool set** — a curated set of tools baked in at compile time; no runtime discovery.
- **Clear user messaging** — when built in AOT mode, any attempt to load an extension fails with a clear error explaining the profile.
- **CI-friendly** — no interactive permission prompts in AOT mode (or all prompts default to a configured policy).

## Notes

- Roslyn scripting and `AssemblyLoadContext` are not AOT-compatible. The extension loading code must be behind a compile-time switch.
- Don't invest heavily in the AOT profile until we know which audience it serves. The JIT profile is the primary target for V1.
- .NET 10 has a good AOT story — worth revisiting once the feature set is stable.
