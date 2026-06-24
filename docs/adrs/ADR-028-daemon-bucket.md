# ADR-028: Personal-Assistant Monorepo Topology — the `daemon/` and `services/` Buckets, the `dcal` Rename, and Swift

**Date:** 2026-06-19
**Status:** Accepted
**Amends:** ADR-025 (D2 bucket set — adds `daemon/` and `services/`; D10 release matrix — server/app artifacts build from `daemon/` and `services/`; D11 landing tiers — `daemon` folds in, the calendar server and the future Dmail server land in `services/`, `dmonium` moves from `frontends/` to `daemon/`; resolves ADR-025 Open Question B for `dmonium` and Swift placement)
**Builds on:** ADR-027 (the `TriageRouter` is application policy in `daemon/Daemon.Routing`, not middleware), ADR-019 (file-based-program composition root), ADR-022 (composition root as a feature), ADR-024 (release-family triggers; protocol-keyed packages vs. independently-versioned app artifacts), ADR-023 (`tools/` extension packages), ADR-005 (config/credentials)

> **Amendment (2026-06-24, change `dmonium-windowed-dashboard`) — framing only:** `dmonium` is now **window-primary** — a `WindowGroup` dashboard with a Dock icon and Cmd-Tab presence (`.regular` activation), plus an **optional, default-off** menu-bar glance. The "always-on, **no-dock host**" rationale (Context §1) and the "**menu bar app**" descriptive labels (Decision D1/D2) are superseded by this framing. **No numbered decision changes** (bucket placement, Swift-in-repo, the `dcal` rename, the release matrix all stand), so this is an in-place amendment, not a superseding ADR.

## Context

ADR-025 consolidated the first-party .NET repos into one monorepo with buckets keyed to ADR-023 roles (`core/`, `providers/`, `tools/`, `middleware/`, `frontends/`, `samples/`, `libs/`). It listed `daemon` under **D11 "keep separate" (empty)**, placed `dmonium` under `frontends/` (D2), and parked both `dmonium`'s placement and the "does the repo go polyglot, or does Swift stay out?" question as **Open Question B**.

Building the Daemon personal assistant surfaces two structural gaps those provisional calls left open:

1. **The Daemon is a cohesive multi-component product, and it introduces Swift.** The `daemon-app` change delivers the C# composition root (`Daemon.cs`), the triage-routing policy library (`Daemon.Routing`, blessed as application code by ADR-027 D5), and the macOS menu bar management app (`Daemon.App`, the `dmonium` product) — the last built with Swift Package Manager, the native macOS idiom for an always-on, no-dock host. ADR-025 never decided whether Swift lives in-repo, and `frontends/` (protocol-surface hosts) is the wrong home for a process-management app.

2. **There is no bucket for backing *server* apps.** Two capabilities — **calendar** and **dmail** — each comprise *two* artifacts: an agent-facing `tools/` extension (the in-process AI tool) **and** a standalone HTTP server that does the real work (sync an iCal feed into SQLite; talk to a mail backend). The `tools/` packages have a home (`tools/Dmon.Tools.Calendar`, `tools/Dmon.Tools.Dmail`). The servers do not: the calendar server was provisionally dropped into `daemon/Daemon.Calendar`, and ADR-025 D11 left the Dmail server out of the monorepo entirely ("the standalone Dmail server is out of scope"). A server is neither a `tools/` package, a protocol-surface host (`frontends/`), nor part of the Daemon composition (`daemon/`); it is a deployable backing service. It needs its own bucket.

ADR-027 already settled the *category* of the router. What remains, and what this ADR decides, is the *physical topology*: a `daemon/` bucket for the Daemon product, a `services/` bucket for backing servers, the naming of the calendar capability, and Swift's place in the repo.

## Decision

1. **`daemon/` is a first-class monorepo bucket holding the Daemon product's composition.** Members: `daemon/Daemon.cs` (composition root, ADR-019), `daemon/Daemon.Routing/` (the `TriageRouter` policy library, ADR-027 D5), and `daemon/Daemon.App/` (the `dmonium` macOS menu bar app). The calendar server does **not** live here (see D3). This **amends ADR-025 D2** (adds `daemon/`) and **D11** (moves `daemon` from "keep separate (empty)" to a populated, folded-in bucket).

2. **`dmonium` lands in `daemon/Daemon.App/`, not `frontends/`.** `frontends/` retains hosts that *are* dmon-protocol surfaces (`Terminal`, `Gateway`, `Desktop`) — processes that speak the JSONL/stdio or gateway wire contract. The menu bar app *manages* the Gateway process (launch/monitor/restart), monitors Tailscale, and edits configuration; it is not a protocol-surface host. The `dmonium` name is preserved as the product name and `.app` bundle identifier (`ai.daemonic.dmonium`). This **amends ADR-025 D2** and **resolves Open Question B** for `dmonium`.

3. **`services/` is a first-class monorepo bucket for backing server apps.** A *service* is a standalone, independently-deployed process that backs an agent capability whose agent-facing surface is a paired `tools/` extension. Members:
   - **`services/Dcal/`** — the iCal-sync HTTP server (renamed and moved from `daemon/Daemon.Calendar`; see D4). Backs `tools/Dmon.Tools.Dcal`.
   - **`services/Dmail/`** — the Dmail mail server. It currently lives in its **own repo** (ADR-025 D11 kept it out of the monorepo); `services/` is its designated home, to be grafted by a later change. Backs `tools/Dmon.Tools.Dmail`.

   Services are **app artifacts, not NuGet packages**: they are not `Dmon.*` first-party packages and are **not** on ADR-024's protocol-lockstep release train; each versions independently in the app-artifact release family (like the Gateway daemon and the dmonium `.app`). They are not protocol-surface hosts (no JSONL/gateway wire contract) and not part of any composition root. This **amends ADR-025 D2** (adds `services/`) and **D11** (the Dmail server's home is `services/`, no longer "out of scope").

4. **The calendar capability is renamed `dcal` across server, tool, and specs.** Consistent with the existing `DCAL_*` configuration prefix already shipped by the `calendar-tool` change:
   - server: `daemon/Daemon.Calendar` → **`services/Dcal`** (project `Dcal.csproj`),
   - tool: `tools/Dmon.Tools.Calendar` → **`tools/Dmon.Tools.Dcal`**,
   - standing specs: `calendar-lookup` → **`dcal-lookup`**, `calendar-sync` → **`dcal-sync`** (and the matching test projects).

   The `calendar-tool` change is already merged/archived; this rename is carried out as part of the `daemon-app` change (which already touches this area), not by reopening the archived change.

5. **Swift is a supported in-repo language, built outside `Everything.slnx`.** `daemon/Daemon.App/` is a standard Swift Package (`Package.swift`). `Everything.slnx` and `daemon/daemon.slnx` are .NET-only and do **not** reference it. Its build path is `swift build -c release` from `daemon/Daemon.App/`, encapsulated by a root `make daemon-app` target; the `daemon/` README documents the split. This **resolves ADR-025 Open Question B's** Swift question: the repo is polyglot for native macOS components, with Swift kept out of the .NET solution graph rather than in a separate repo. `dmon-swift` (the prospective Swift *client* SDK for the AR body) remains a separate, open question and is **not** decided here.

6. **Release matrix records artifact sources only; no new family, no trigger change.** ADR-025 D10's app-artifact family already lists the Gateway publish and the dmonium `.app`/`.dmg`. This ADR records that the dmonium artifact builds from `daemon/Daemon.App/` (via `make daemon-app` on a macOS runner) and that `services/` members (`Dcal`, future `Dmail`) are app-artifact deployables (container/publish), each independently versioned. ADR-024's per-package NuGet triggers are untouched. Code-signing/notarisation stays a deferred follow-on. This **amends ADR-025 D10** only as to artifact source locations.

7. **Both new buckets follow ADR-025's existing workflow rules unchanged.** Each gets its own `.slnx` (`daemon/daemon.slnx`, `services/services.slnx`) for the C# projects, nested `Directory.Build.props` importing root, and its own `openspec/` for component-local capabilities (ADR-025 D3/D5/D6/D8). Component changes confined to a bucket run in a per-area worktree (`change/daemon-<slug>`, `change/services-<slug>`); cross-cutting changes stay on `main` (D7). Path-filtered CI treats `daemon/**` and `services/**` as normal downstream areas (`core/ ⇒ all` still holds; D9). `middleware/` **stays empty** — none of these components is an `IDmonMiddleware` (ADR-026 D4; ADR-027 upheld).

## Consequences

- **A clean tool/service split.** Each of calendar and dmail now has an unambiguous home for both halves: the agent-facing extension in `tools/`, the backing server in `services/`. The "where does the server go?" ad-hoc placement in `daemon/` is corrected.
- **The Daemon product is cohesive but not a junk drawer.** `daemon/` holds the composition + routing + management app — the things specific to *being the Daemon agent* — and not the general-purpose servers it happens to consume.
- **The repo is polyglot, with a sharp boundary.** Swift is invisible to `dotnet`/`Everything.slnx`, reached only via `make daemon-app`. One extra rule for contributors.
- **`frontends/` keeps a sharp meaning** — protocol-surface hosts only. A management app and a backing server are no longer mistaken for protocol hosts (the ADR-026/027 category discipline, applied to topology).
- **The `dcal` rename touches already-merged code.** `tools/Dmon.Tools.Calendar` and the `calendar-lookup`/`calendar-sync` standing specs (just synced from the archived `calendar-tool` change) are renamed under `daemon-app`. One-time churn; `DCAL_*` config is already consistent with the new name, so there is no env/config migration.
- **No release-train disturbance.** Services and the menu bar app are app artifacts, independently versioned; ADR-024's protocol-lockstep package set is unchanged.
- **ADR-025 Open Question B is mostly closed** — `dmonium` placement, the Swift question, and the Dmail-server home are resolved; `dmon-swift` and `memlite`/`memlite-pi` remain open.

## Alternatives

- **Leave the calendar server in `daemon/` (status quo of the merged change).** Rejected: it conflates a reusable backing service with the Daemon's own composition, and gives dmail's server (a sibling in every respect) no parallel home. A `services/` bucket names the pattern once.
- **Put the servers in `tools/` alongside their extensions.** Rejected: `tools/` is ADR-023's bucket for `Dmon.Tools.*` *packages* (NuGet, protocol-keyed, in-process `IToolExtension`s). A standalone HTTP server is an app artifact with independent versioning and a deployment story; co-locating it would muddy `tools/`'s package contract and ADR-024's release model.
- **Keep `dmonium` in `frontends/` (ADR-025 D2 as written).** Rejected: splits one product across buckets and conflates process management with protocol hosting.
- **Keep the Daemon/servers/Swift in separate repos (ADR-025 D11 "keep separate").** Rejected: the Daemon depends on `core/`, the two `tools/` extensions, the two `services/` servers, and `memory/Dmon.Memory.Meko`; splitting it out reintroduces cross-repo coordination for exactly the dependencies the monorepo consolidated.
- **Add the Swift package to `Everything.slnx`.** Rejected: `.slnx` is a .NET solution format; a Swift Package is not an MSBuild project. `make daemon-app` is the honest seam.
- **Keep the name "Calendar".** Rejected: the shipped config surface is already `DCAL_*`; `dcal` (and `dmail`) give the two personal-assistant services a consistent `d<thing>` identity, and align the server/tool/spec names.

## Open Questions

- **A. `dmon-swift` (Swift client SDK) placement.** This ADR establishes Swift *can* live in-repo (`daemon/Daemon.App/`). Whether the prospective AR-body client SDK lands in `daemon/`, a polyglot `frontends/`, or stays separate is deferred to its own change. (Carried over from ADR-025 Open Question B.)
- **B. Dmail-server graft.** `services/` is the Dmail server's designated home, but the actual `git filter-repo` graft from its standalone repo is a later change (mechanics per ADR-025 D13). This ADR only reserves the home.
- **C. Code-signing / notarisation pipeline.** Required before Keychain-backed key storage and distributable artifacts. Deferred to a follow-on CI change (ADR-025 D10 family). Until then, dev builds are unsigned.

## Relationship to other ADRs

- **ADR-025** — amended: D2 (adds `daemon/` and `services/`; moves `dmonium`), D10 (artifact source locations for dmonium and the services), D11 (`daemon` folds in; calendar server → `services/`; Dmail server home → `services/`); resolves Open Question B for `dmonium` and Swift. All other ADR-025 decisions (workflow, CI filter, intra-repo `ProjectReference`, per-area `.slnx`, openspec/ADR boundary rule, import mechanics) apply to the new buckets unchanged.
- **ADR-027** — honoured and given a physical bucket: the routing policy lives in `daemon/Daemon.Routing`, exactly the application code ADR-027 D5 placed with the agent.
- **ADR-024** — unaffected: services and the menu bar app are app artifacts, independently versioned; the protocol-lockstep `Dmon.*` package set and its triggers are unchanged.
- **ADR-026** — upheld: `middleware/` stays empty; none of these components is an `IDmonMiddleware`.
- **ADR-023** — honoured: `tools/` keeps its `Dmon.Tools.*` package contract; the renamed `tools/Dmon.Tools.Dcal` is an ordinary tools package, and the server it pairs with lives in `services/`, not `tools/`.
- **ADR-019 / ADR-022 / ADR-005** — `daemon/Daemon.cs` is an ordinary file-based-program composition root reading config/credentials per ADR-005; this ADR only gives it a home.
