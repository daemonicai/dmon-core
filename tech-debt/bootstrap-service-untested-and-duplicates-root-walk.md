# `BootstrapService` is untested and duplicates the resolver's root walk

**Status:** open
**Where:** `core/Dmon.Core/Bootstrap/BootstrapService.cs` (`DmonRootExists`, `:76-107`)
against `core/Dmon.Core/Session/SessionDirectoryResolver.cs` (`FindDmonRoot`)
**Surfaced:** 2026-09-10, section-1 supervisor of `session-root-resolution`
(verified: no test references `BootstrapService`)
**Severity:** low, but it is the same class of drift `session-root-resolution` exists to fix

## What

- **Untested.** Nothing in `test/` exercises `BootstrapService`. It reads
  `Environment.SpecialFolder.UserProfile` and `Directory.GetCurrentDirectory()`
  directly, so a test has no way to substitute either without writing into the real
  home directory. The `session-storage` spec's scenario *"First-use bootstrap creates
  ~/.dmon/"* therefore has no test behind it.
- **Duplicated logic.** `DmonRootExists` re-implements the resolver's walk-up (looking
  for `.dmon/config.yaml`) with its own copies of the `.dmon`, `config.yaml` and
  `sessions` constants. If the marker rule ever changes in one place, the other can
  silently disagree. Bootstrap would then decide "no root, create `~/.dmon`" by a
  different rule from the one session resolution uses.

## What to do

1. Inject the home and working directories into `BootstrapService` (or a
   `TimeProvider`-style seam), and test the bootstrap scenario against a temp home.
2. Have `BootstrapService` ask the resolver (or a shared marker helper) whether a root
   exists, instead of walking the tree itself.
