# ADR-019: Composition-Root Hosting — `Dmon.cs` as the Core Entry Point

**Date:** 2026-06-13
**Status:** Accepted
**Supersedes:** ADR-009 in full; ADR-011 Decisions 2–4 (the host-bundled runtime acquisition path and the runnable-closure package shape) and ADR-008's *dynamic-load mechanism* (runtime `extension.load`, reflection discovery into the Default ALC) plus ADR-002's `.csx` tier and `promote` path.
**Retains:** the `IDmonExtension` / `AIFunction` contract (ADR-002 core), ADR-008's "one shared load context, restart-as-reclaim" principle, and ADR-011's protocol-keyed versioning + contract packages + cadence-independence (Decisions 1, 5, 6, 8, 9).
**Reframes:** ADR-006's extension-loading permission tier.
**Builds on:** .NET 10 *file-based programs* (`dotnet run app.cs`).

## Context

Three independent mechanisms exist today to answer one question — *what code makes up this agent, and how does it get here?*

- **Runtime core acquisition (ADR-011).** `dmoncore` ships as a runnable framework-dependent publish closure (D4) that the `dmon` host fetches at runtime into the global NuGet cache via the `NuGet.Protocol` SDK (D3), then spawns over JSONL/stdio (ADR-003).
- **Config-driven extension loading (ADR-009).** The "active" extension set is a `union`-merged list across `~/.dmon` and `./.dmon` `config.yaml`, loaded at `dmoncore` startup by reflection into the Default `AssemblyLoadContext` (ADR-008). `/reload` is a process restart.
- **Dynamic load (ADR-002 `.csx` tier + ADR-006 gate).** `.csx` scripts hot-load via `Dotnet.Script.Core` (whose embedding API ADR-002 itself flags as *spike-required*), and runtime `extension.load` is screened by an elaborate source-fetch-from-nuspec + LLM-source-analysis + confirm pipeline (ADR-006 "Extension loading").

That is three subsystems, one undocumented dependency, a type-identity hazard (ADR-008 Context), and a bespoke runtime NuGet downloader — all to compose a process whose composition rarely changes mid-session anyway (ADR-008/009 already made reload a restart between turns).

.NET 10 ships **file-based programs**: a single `.cs` file with `#:package`, `#:sdk`, and `#:property` directives is compiled as a *virtual project* by the SDK, with normal NuGet restore. Confirmed against the SDK design doc (`dotnet/sdk` `documentation/general/dotnet-run-file.md`): a file with no entry point builds as a library via `#:property OutputType=Library`; `dotnet build file.cs` builds without running; the build emits a normal `.deps.json` dependency closure; an up-to-date check skips rebuilds when nothing changed; `#:include`/`#:ref` allow shared/multi-file composition. A file-based build's `.deps.json` is exactly the input ADR-008's loader wanted and the `.csx`/Dotnet.Script path never cleanly produced.

This ADR replaces all three mechanisms with one: **the user authors the composition root, and the SDK builds it.**

## Decision

1. **`dmoncore` is a framework library, not the entry point.** It exposes a hosting surface — `DmonHost.CreateBuilder(args)` → a builder (provider/model, extensions, permission mode, profile) → `.Build().RunAsync()`, where `RunAsync()` *is* the JSONL/stdio core loop. The wire contract (ADR-003), session storage (ADR-004), and the `IChatClient` permission pipeline (ADR-002/006) are unchanged; only the *entry point* moves out of the package.

2. **`Dmon.cs` is the composition root.** A file-based program in the working directory declares the core and its extensions and wires them:

   ```csharp
   #:package dmoncore@0.1.*
   #:package Acme.DmonExt.Postgres@2.1

   using Dmon.Hosting;

   await DmonHost.CreateBuilder(args)
       .AddExtension<PostgresExtension>()
       .Build()
       .RunAsync();
   ```

   `Dmon.cs` compiles to the core executable. Extensions are **compile-time dependencies** (`#:package` + builder calls), not runtime loads.

3. **The dynamic-load tier is removed.** No runtime `extension.load`, no `.csx` tier, no config-driven extension list, no reflection discovery, no `Dotnet.Script.Core`, no `promote` path. Adding an extension is editing `Dmon.cs` (a `#:package` line + a builder call) and reloading. This is the single, surviving subtraction that motivates the ADR.

4. **The host builds `Dmon.cs` in a separate step, then runs it with the build suppressed (`dotnet run --no-build`) — the build phase never shares the wire.** Because the child's **stdout is the ADR-003 wire channel**, no MSBuild/restore output may touch it. The host first runs `dotnet build Dmon.cs` as a *distinct* process whose stdout/stderr it captures or discards — and this same step **is the staleness gate**: the SDK's incremental up-to-date check rebuilds only when the `#:package` set or any `.cs` changed (Decision 8), and is a near-no-op otherwise. It then launches **`dotnet run Dmon.cs --no-build`** as the stdio child. `--no-build` skips the build phase entirely (and implicitly sets `--no-restore`), so no build or restore line can precede the program on stdout — the child's stdout carries **only** JSONL frames — while the SDK still owns binary location, `runtimeconfig`, and launch, so **dmon manages no artifact paths of its own** (this is what dissolves the old "where do the binaries live" question). A bare `dotnet run Dmon.cs` is rejected: it builds inline and interleaves that output onto stdout, corrupting the `agentReady` handshake — and not only on first run, since `/reload` rebuilds on any composition change. The terminal logger is a red herring here: under a redirected (non-TTY) pipe it auto-disables, leaving the plain console logger writing build status to stdout regardless of `--tl:off`/`--verbosity` — so the fix is to remove the build *phase* from the wire process (`--no-build`), not to re-skin its output. (`--tl:off` is still worth passing to the **build** step to keep the host's captured build logs readable.) Should the file-based-program `dotnet run … --no-build` path ever prove non-silent on stdout, launching the built assembly directly is the stricter fallback. The terminal/desktop host and ADR-012's gateway drive this via the `ICoreLauncher`/`ICoreProcess` seam; everything above the socket is identical to today — what changes is only *what is on the other end of the pipe*, a user-composed core rather than a stock binary.

5. **Launcher precedence, file-before-env.** `./Dmon.cs` (or the agent selected per ADR-020) **wins over** `$DMON_CORE`, which wins over a **built-in default composition**. The default is the prebuilt stock core (ADR-011's runnable closure, retained for this path) so an empty directory / first run *just works* with no SDK and no authored C#. `dmon init` scaffolds an editable `Dmon.cs` for users who want to customise. Authoring `Dmon.cs` is the deliberate, opt-in customisation path.

6. **Config is a source; composition code is sovereign.** `config.yaml` (both ADR-009 scopes — *retained for settings, not extension lists*), env, and CLI are exposed to `Dmon.cs` through the builder's configuration, but the code may read them or override them outright (e.g. hardcode `.UseOllama("gemma4:26b")`). This is the law at every level: **markdown/YAML declares, C# overrides.**

7. **Acquisition is `dotnet restore` over the `#:package` set.** dmon ships no runtime NuGet downloader (supersedes ADR-011 D3). ADR-011's protocol-keyed versioning survives intact: `#:package dmoncore@0.1.*` pins the compatible protocol line (ADR-011 D5), and the `agentReady` `protocolVersion` compatibility gate (ADR-011 D6) still fires against the resolved core.

8. **`/reload` re-runs `Dmon.cs`.** Restarting the core re-restores if the `#:package` set changed and recompiles if any `.cs` changed (SDK incremental cache makes the no-change case cheap). This is the same restart-between-turns boundary ADR-008/009 already established.

9. **`dmoncore` is one **library package**; the stock default is a prebuilt instance of it.** The unit of distribution is the `dmoncore` **library** (`#:package`-able, Decision 2). The no-SDK / first-run **stock default core** (Decision 5) is *not* a separate codebase: it is a **prebuilt framework-dependent publish of a canonical `Dmon.cs`** that references the library with no extra extensions. The default path *is* the `Dmon.cs` path, built ahead of time and shipped so an empty directory runs on the .NET runtime alone. ADR-011 D4's runnable closure survives only in this form — a convenience artifact *derived from the library*, no longer the unit of distribution — which is why ADR-011 D4's closure-as-primary-shape is superseded while the no-SDK experience is preserved. Authoring a `Dmon.cs` is then "rebuild what the default already is, with additions": one mechanism at two build-times, not two systems.

## Consequences

- **Large net subtraction.** Removes `Dotnet.Script.Core` and its spike, the runtime NuGet downloader, ALC reflection-discovery and the type-identity hazard, the config-driven loading machinery, the `promote` path, and the *primary trigger* of ADR-006's extension-load gate. The riskiest machinery in the design — the agent loading untrusted code into its own process at runtime — is replaced by "compile the agent from a declared package set."
- **Reproducible and per-project.** `Dmon.cs` commits with the repo and pins core + extension versions; a teammate runs `dotnet run Dmon.cs` and gets an identical agent. The `config.yaml`-list model never gave this cleanly.
- **A .NET SDK becomes required for the `Dmon.cs` path.** ADR-011 assumed only the .NET *runtime* is present; building `Dmon.cs` (Decision 4) needs the **SDK**. The stock-default path (Decision 5) preserves the runtime-only experience, so this requirement falls only on users who author a `Dmon.cs`. Honestly a new dependency, mitigated, not free.
- **First run / restore cost.** A new `#:package` set restores on next reload; unchanged sets are SDK-cache hits and `.cs`-only edits drop to `csc`. Consistent with the restart-as-reload model.
- **Mid-session agent-authored tools are lost.** ADR-002 valued `.csx` scripts written *mid-session* and usable *that turn*. The replacement is edit-`Dmon.cs`-then-reload (available next turn). ADR-008 already conceded same-turn dynamism by making reload a restart, so the marginal loss is "next turn vs this turn," in exchange for a diffable, committable, sandbox-free self-extension loop.
- **ADR-011's rejected library alternative is revived on new grounds.** ADR-011 rejected "publish `dmoncore` as a plain library" because *`dmon` itself* would have to run `RestoreCommand` and compose a `runtimeconfig`. File-based programs delegate that to the SDK (`dotnet run`), so the rejection's premise no longer holds — the machinery is the SDK's, not dmon's.

## Alternatives

- **Adopt file-based programs only for the `.csx` tier, keep everything else.** This was the first framing explored and rejected: it leaves the runtime downloader, the config-list loader, and the dynamic-load gate all in place, missing that one authored file answers *acquisition, composition, and reload* together.
- **Keep `dmoncore` as the runnable-closure entry point; layer `Dmon.cs` on top as an optional override.** Rejected as the primary model — it keeps two composition systems. Retained only as the *default/no-SDK fallback* (Decision 5), not as the main path.
- **Have `dmon` parse `#:` directives and generate a virtual `.csproj` itself** (the part-2 internals show this is ~a dozen lines). Rejected for V1: leaning on the first-party SDK build is simpler and tracks the feature; self-parsing is a fallback if the CLI dependency ever bites.

## Open Questions

- **A. ~~`dmoncore`'s dual packaging.~~** *Resolved by Decision 9 — a single `dmoncore` library package; the stock default is a prebuilt publish of a canonical `Dmon.cs` that references it, so "default" and "authored" are the same mechanism at two build-times.*
- **B. SDK detection and fallback.** How the host detects an absent .NET SDK and whether it falls back to the stock core or surfaces an actionable error when a `Dmon.cs` is present but unbuildable.
- **C. ~~Build-artifact location.~~** *Resolved by Decision 4.* `dotnet build` then `dotnet run --no-build` leaves binary location, `runtimeconfig`, and cleanup to the SDK's managed output (dmon owns no build directory), and `dotnet build`'s incremental check is the staleness gate. The only thing to confirm at implementation: that `dotnet run Dmon.cs --no-build` for the file-based-program path emits nothing on stdout ahead of the program (and add `--verbosity quiet` / fall back to launching the built assembly directly if it does).
- **D. ~~Agent-edits-its-own-`Dmon.cs`.~~** *Resolved by ADR-021.* The agent self-modifying its composition root (adding a `#:package`, editing the builder wiring) is gated by a new apex **`compose`** permission tier amending ADR-006: the gate fires at the build/reload chokepoint on an agent-initiated composition change, itemizes new packages (approved by exact pin), is never globally suppressible, and parks (never auto-approves) in a headless/remote session.
- **E. ~~Gateway working directory.~~** *Resolved via ADR-020 + ADR-021.* A remote/gateway session has no client filesystem, so it composes not from a "cwd" but from the **agent definition selected at `createSession`** (ADR-020), resolved under the **gateway's configured workspace root** — never a client-supplied path. Any `compose` change in such a session **parks** (ADR-021 Decision 6 / park-while-detached), there being no human at the keyboard.

## Relationship to other ADRs

- **ADR-002** — the `IDmonExtension`/`AIFunction` contract is retained and is now consumed at compile time via the published `Dmon.Abstractions`/`Dmon.Extensions` packages; the `.csx` tier and `promote` path are superseded.
- **ADR-003 / ADR-004** — wire protocol and session storage are untouched; the core process is the same stdio peer, differently produced.
- **ADR-006** — the extension-loading multi-step gate loses its runtime trigger; trust shifts to author-time (you committed `Dmon.cs` and its `#:package` set, as for any `dotnet` project). The new gate surface is the agent modifying its own composition root — gated by the apex `compose` tier in **ADR-021**, which amends ADR-006 and closes Open Question D.
- **ADR-008** — the *reflection-discovery / dynamic-load* mechanism is superseded; its deeper principle (one shared context, restart-as-reclaim) is retained and in fact strengthened — a compiled closure has a single, SDK-resolved identity graph, eliminating the type-identity hazard outright.
- **ADR-009** — superseded in full: composition is code, not a config-driven list. The two-scope `config.yaml` merge survives for *settings* (ADR-013), not for extensions.
- **ADR-011** — Decisions 2–4 (host-bundled acquisition, runnable-closure-not-library) are superseded for the `Dmon.cs` path; the protocol-keyed versioning, contract packages, cadence-independence, and skew guard (D1, D5, D6, D8, D9) are retained and reused by `#:package dmoncore@<protocol>.*`.
- **ADR-012** — the `ICoreLauncher`/`ICoreProcess` seam now builds `Dmon.cs` then launches it with `dotnet run --no-build` (Decision 4 — the build phase is kept off the wire so the child's stdout stays pure JSONL); the gateway, transport, and resume protocol are unchanged.
- **ADR-013 / ADR-020** — profiles remain the per-session selection surface; ADR-020 generalises them into `.md` + `.cs` agent definitions built on this hosting model.
