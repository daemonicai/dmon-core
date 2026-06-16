# DEVLOG — graft-dmail

Phase 2 monorepo satellite graft: import the dmon Dmail **tool extension** into `tools/Dmon.Tools.Dmail`, following the Phase 1 (`graft-llamacpp-provider`) recipe plus an ADR-022 API port. The standalone Dmail server stays in its own repo (extension-only scope).

## Status

- [x] Group 1 — History-preserving import
- [ ] Group 2 — Rename to the tool family (Dmon.Tools.Dmail)
- [ ] Group 3 — API port to IToolExtension / Dmon.Abstractions
- [ ] Group 4 — Re-wire to monorepo conventions (ProjectReference, CPM, fresh test csproj)
- [ ] Group 5 — Solutions
- [ ] Group 6 — Verification gates

---

## Group 1 — History-preserving import

**Source:** local-only `dmail` repo, branch `feat/dmon-tool-dmail` @ `b1af562` (the extension is *not* on dmail's `main` — it lives only on this branch, 2 commits ahead). The extension itself was added upstream in a single commit (`7556790 feat: add Dmon.Extensions.Dmail dmon extension package`), so that one commit is its complete history.

**What landed:** a two-parent merge commit importing exactly 7 files — `tools/Dmon.Tools.Dmail/{DmailApiException,DmailClient,DmailExtension,DmailModels}.cs`, `Dmon.Extensions.Dmail.csproj` (filename renamed in Group 2), `README.md`, and `test/Dmon.Tools.Dmail.Tests/DmailExtensionTests.cs`. No server code, server tests, satellite `openspec/`, `nuget.config`, vendored nupkgs, or `Directory.*` plumbing.

**Decisions / lessons:**
- **`git clone` for filter-repo needs `--no-local -b <branch>`.** The design's recipe omitted these. A plain local clone is hardlinked (not "freshly packed") and a post-clone `git checkout` adds a second HEAD reflog entry — filter-repo aborts on both ("not a fresh clone" / "expected at most one entry in the reflog for HEAD"). Cloning the target branch directly with `--no-local -b feat/dmon-tool-dmail` satisfies both safety checks. (Recorded so the next tool/extension graft skips the two false starts.)
- The grafted files are **inert** after import: not referenced by any `*.slnx`, so `dotnet build Everything.slnx -c Release` stays green (0 warnings, 0 errors). Rename/port/CPM/slnx wiring are Groups 2–5.
- The imported test (`DmailExtensionTests.cs`) is genuinely server-independent — references only the extension namespace, `Microsoft.Extensions.AI`, and `Dmon.Protocol.Enums`; it constructs `DmailExtension` against an unreachable port. Its `.csproj` is authored fresh in Group 4 (the satellite's test project referenced `src/Dmail`, which is not grafted).

**Gates:** `Everything.slnx` build green; history verified via `git log --follow tools/Dmon.Tools.Dmail/DmailExtension.cs`; reviewer audit PASS (no blockers, no nits). Stale names/references (`Dmon.Extensions.Dmail`, `IDmonExtension`, `using Dmon.Extensions`, the `Dmon.Extensions` PackageReference, standalone `<Version>`, `Daemonic.Dmail.*` namespaces) are deferred-by-design to Groups 2–4.
