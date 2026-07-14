## 1. Fail closed on unresolvable symlinks (core)

- [x] 1.1 In `core/Dmon.Core/Permissions/SandboxContainmentChecker.cs` `FollowLinkToTarget`, add an existence guard **after** the relative-path re-anchoring block: when `final` is non-null but `Path.Exists(final)` is false, return `null` (fail closed). This makes a dangling symlink that resolves without throwing (Linux) fail closed identically to the platforms that throw (macOS/Windows). Do not change `ResolveRealPath`, `ResolveExistingAncestor`, or `IsSymlink` — they already treat a `null` return as reject.
- [x] 1.2 Correct the backwards platform comment in `FollowLinkToTarget` (currently "On Linux it throws IOException for dangling symlinks; on macOS it returns the (non-existent) target path"): state the CI-verified reality — macOS/Windows throw `IOException` for a dangling link, Linux returns the non-existent target without throwing, and the explicit `Path.Exists(final)` guard fails both closed on every platform.

## 2. Tests

- [x] 2.1 In the `Dmon.Core.Tests` sandbox-containment suite, add a test that a target whose **leaf** is a dangling symlink (points at a non-existent path) inside the sandbox root is treated as **not contained** (containment resolution returns `null` / caller rejects).
- [x] 2.2 Add a test that a target reached through a **dangling symlinked ancestor** directory is treated as **not contained**.
- [x] 2.3 Add a regression test that a **live, resolvable** in-sandbox symlink still resolves and is treated as **contained** (guards against an over-broad guard that would reject valid links), and — where the existing suite covers it — that a live symlink whose target escapes the sandbox is not contained.
- [x] 2.4 Note in the test file (comment) and in the DEVLOG that the fix is only observable on Linux/Ubuntu CI — macOS already fails closed via the throw, so a green local `make test` does not prove the fix; **CI is the real verification** (per the batch's standing symlink/filesystem lesson).

## 3. Gates and spec alignment

- [x] 3.1 `make build` clean (no warnings; `TreatWarningsAsErrors`).
- [x] 3.2 `env -u MEKO_API_KEY make test` green — the new tests plus all existing tests.
- [x] 3.3 `openspec validate sandbox-symlink-fail-closed --strict` passes; the `permission-model` delta (one ADDED requirement) matches the implemented behaviour.
