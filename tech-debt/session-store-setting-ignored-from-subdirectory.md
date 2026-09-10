# A project root's `sessionStore` is ignored when dmon starts in a subdirectory

**Status:** open, and **undecided**: it is not yet settled whether this is a defect or the rule
**Where:** `core/Dmon.Core/Hosting/DmonHostBuilder.cs:44-50` against
`core/Dmon.Core/Session/SessionDirectoryResolver.cs:24-38`
**Surfaced:** 2026-09-10, section-1 supervisor of `session-root-resolution`
(verified against the code by the Architect)
**Severity:** low today (few projects set `sessionStore`), but the spec cannot state the rule
until it is decided

## What

Session discovery has two halves that read from different places:

- **The root marker** is found by walking up from the working directory for
  `.dmon/config.yaml` (`SessionDirectoryResolver.FindDmonRoot`).
- **The `sessionStore` setting** is read from the merged `IConfiguration`, which
  `DmonHostBuilder` layers from `~/.dmon/config.yaml` < **`<CWD>`**`/.dmon/config.yaml` <
  **`<CWD>`**`/.dmon/config.local.yaml`. The discovered root's own file is loaded only
  when the working directory *is* the root.

So with `sessionStore: global` (or a path) in `<root>/.dmon/config.yaml`, starting dmon
in `<root>` honours it, but starting in `<root>/src` ignores it. Sessions then go to
`<root>/.dmon/sessions/`, or wherever `~/.dmon/config.yaml`'s `sessionStore` says.

ADR-004's discovery step 1 reads *"Walk up from CWD for .dmon/config.yaml → read
sessionStore"*, which says the setting comes from the discovered file. The code has
never done that from a subdirectory.

## Why it is parked, not fixed

`session-root-resolution` set out to change no runtime behaviour. On 2026-09-10 the
Product Owner chose to have the spec state only what is both true and decided. So its
discovery requirement says explicitly that the subdirectory case is **not specified**,
and ADR-004's amendment note points here.

## What to do

1. **Decide the rule.** Should a root's `.dmon/config.yaml` apply from any directory
   beneath it, as ADR-004 step 1 says?
2. If yes, the likely fix is for `DmonHostBuilder` to layer the **discovered root's**
   `.dmon/` files instead of the working directory's. That touches **all** project
   config (providers, `activeModel`, system prompt), not just sessions, and
   `ActiveModelStore`, which writes `./.dmon/config.local.yaml` relative to the working
   directory. It needs its own change with a design, not a one-line patch.
3. Then specify the subdirectory case in the `session-storage` spec, and remove
   ADR-004's "known gap" sentence.
4. At the same time, decide whether `sessionStore: <absolute path>` is supported.
   ADR-004 names it, and the resolver's `switch` implements it. But the
   `session-root-resolution` spec deliberately covers only `local` and `global`, and
   no test exercises the path form.

Related: [`$HOME` is a project root once bootstrapped](home-dmon-config-makes-home-a-project-root.md).
