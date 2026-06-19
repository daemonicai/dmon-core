# DEVLOG — daemon-app

Narrative companion to `tasks.md`. Newest entries at the bottom of each section.

## Pinned decisions (read before planning any block)

- **ADR-028 ACCEPTED** (`docs/adrs/ADR-028-daemon-bucket.md`, indexed in `CLAUDE.md`). It expanded the originally-scoped "daemon bucket" decision into the full personal-assistant topology:
  - **`daemon/` bucket** = Daemon *composition only*: `Daemon.cs`, `Daemon.Routing`, `Daemon.App` (dmonium). **Not** a backing server.
  - **NEW `services/` bucket** = standalone backing servers that pair with a `tools/` extension. Members: `services/Dcal` (the iCal-sync HTTP server, **moved+renamed** from `daemon/Daemon.Calendar`) and the future `services/Dmail` (grafted later from its own repo). Services are **app artifacts, independently versioned, NOT NuGet/protocol-keyed** (off ADR-024's lockstep train).
  - **`dmonium` → `daemon/Daemon.App`** (not `frontends/`, which stays protocol-surface hosts).
  - **`dcal` rename** (matches the shipped `DCAL_*` config): server `services/Dcal` (`Dcal.csproj`), tool `tools/Dmon.Tools.Dcal` (verb `AddDcalAbilities()`, types `DcalExtension`/`DcalAbilityProvider`), standing specs `dcal-lookup`/`dcal-sync`. **Model-facing tool names (`lookup_calendar`, `list_upcoming_events`) and the `DCAL_*` prefix are UNCHANGED** — rename identifiers only, keep domain words.
  - **Swift** is supported in-repo but **outside** `Everything.slnx`/`daemon.slnx`; built via `make daemon-app`.
- **Scope decision (user):** the `services/` bucket + `dcal` rename are **folded into this change** (not a separate change). That is why `tasks.md` gained section 2 and the spec set gained a `monorepo-layout` MODIFIED delta.
- **Resolved Open Questions:** OQ-A (ADR number) → new **ADR-028**, Accepted. ADR-025 OQ-B → resolved for dmonium + Swift + Dmail-server home; `dmon-swift` SDK still open.
- **Prereqs present (confirmed):** Phase 1 seams `ITerminalClientFactory`, `IAbilityProvider`, `AbilityRegistry`, `DaemonTelemetry` all exist in `core/`. Phase 2 calendar code exists: `tools/Dmon.Tools.Calendar` and `daemon/Daemon.Calendar` (the latter about to move to `services/Dcal`), both already in `Everything.slnx`.
- **Standing specs `calendar-lookup`/`calendar-sync` NOT yet renamed** — left matching the current (pre-rename) code; task 2.6 renames them in lockstep with the code rename (task 2.4). Don't be surprised they still say "calendar".
- **Gotcha:** `make test` hangs ~90s if `MEKO_API_KEY` is set (live Meko smoke). Brief workers to run `env -u MEKO_API_KEY make test`.

## Section 1 — ADR-028 (DONE)

- 1.1/1.2 complete: ADR-028 authored and accepted by the user (after two revisions — the services bucket + dcal rename were added at the user's direction during review); `CLAUDE.md` ADR index row added. The change's `proposal.md`/`design.md`/`tasks.md`/specs were realigned to the expanded scope and `openspec validate daemon-app --strict` passes.

## Section 2 — services/ bucket + dcal rename (DONE)

- **2.1–2.7 complete; reviewer APPROVED (no blockers).** All renames landed as `git mv` R100 (history preserved). Gates green: `make build` 0/0, `env -u MEKO_API_KEY make test` all pass (Dcal.Tests 13/13, Dmon.Tools.Dcal.Tests 14/14, Core 591, Terminal 187, Gateway 208, Mtplx 32), `validate --strict` valid.
- Final layout: server `services/Dcal` (`Dcal.csproj`, `Sdk=Microsoft.NET.Sdk.Web`, `InternalsVisibleTo=Dcal.Tests`); tool `tools/Dmon.Tools.Dcal` (verb `AddDcalAbilities()`, types `DcalExtension`/`DcalAbilityProvider`, file `DcalRegistrationExtensions.cs`, namespace `Dmon.Hosting` retained); specs `dcal-lookup`/`dcal-sync`; new root `services.slnx` + `services/README.md`; `daemon.slnx` reduced to a valid EMPTY `<Solution>` (section 3 repopulates with `Daemon.Routing`); `Everything.slnx` `/daemon/` folder now empty, `/services/` folder added.
- **Kept (domain words, NOT renamed):** tool names `lookup_calendar`/`list_upcoming_events`, `DCAL_*` prefix, domain types `CalendarEvent`/`CalendarClient`/`ICalendarStore`/`CalendarApiException`/`CalendarRow`/`CalendarDatabase`/`CalendarSyncService`, test fixture class names (`CalendarExtensionTests` etc.), and `DcalExtension.Name => "Calendar"`. Reviewer confirmed this is correct per "rename identifiers only."
- For §3's architect: `daemon.slnx` exists at root but is currently EMPTY; §3 adds `Daemon.Routing` to it. `daemon/` currently holds only `BRIEF.md` (pre-existing tracked file).

- **Convention ruling (orchestrator, verified against the repo):** all area `.slnx` live at **repo ROOT** (`core.slnx`, `daemon.slnx`, `tools.slnx`, …) — there are **no in-bucket `.slnx`**, and there is a **single root `Directory.Build.props`**. So `services.slnx` is created at root (not `services/services.slnx`), and **no** `services/Directory.Build.props` is added (root covers it; ADR-025 D8 makes per-area props optional). Tasks 2.1/3.1/3.2 were corrected from the original in-bucket wording to match this. `daemon.slnx` **already exists** at root (block-1 architect's "no daemon/daemon.slnx" was about the in-bucket path only). NOTE for §3's architect: task 3.4 still calls for a `daemon/Directory.Build.props` to default `Daemon.*` assembly names — that IS a legitimate divergent per-area prop, so it's allowed; it would be the repo's first per-area props file.
