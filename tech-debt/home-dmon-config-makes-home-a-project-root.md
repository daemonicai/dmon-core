# `~/.dmon/config.yaml` makes `$HOME` itself a project root

**Status:** open
**Where:** `core/Dmon.Core/Session/SessionDirectoryResolver.cs:41-65` (walk-up) together
with `core/Dmon.Core/Bootstrap/BootstrapService.cs` (which creates `~/.dmon/config.yaml`)
**Surfaced:** 2026-09-10, section-1 supervisor of `session-root-resolution`
(inferred from the code, not reproduced)
**Severity:** low. The resulting path is usually the same, but a documented setting behaves
differently depending on where dmon starts.

## What

The root marker is any `.dmon/config.yaml` found walking up from the working directory.
`~/.dmon/config.yaml` qualifies, and bootstrap guarantees it exists. So for **any**
working directory under `$HOME`, the walk-up finds `$HOME` as a project root whenever no
nearer root exists.

- Inside `$HOME`, "no root found → `~/.dmon/sessions/`" never happens. Resolution goes
  "root = `$HOME` → `$HOME/.dmon/sessions/`", which is the same path, so nothing visibly
  breaks.
- **Where it diverges:** a custom `sessionStore: /some/path` in `~/.dmon/config.yaml` is
  honoured when dmon starts under `$HOME`, but ignored when it starts outside `$HOME`
  (for example `/tmp`, `/Volumes/…`, or `/private/var/folders/…` for test temp
  directories). There no root is found, and the resolver returns the hard-coded
  `~/.dmon/sessions/` without reading `sessionStore` at all.

This predates `session-root-resolution` and was not changed by it.

## What to do

1. Decide whether the global config should count as a project marker. The likely answer
   is no: exclude `~/.dmon/` from the walk-up, and make the no-root fallback honour the
   global `sessionStore` instead of hard-coding the path.
2. Decide together with
   [a root's `sessionStore` ignored from a subdirectory](session-store-setting-ignored-from-subdirectory.md),
   since both are about where `sessionStore` is read from.
