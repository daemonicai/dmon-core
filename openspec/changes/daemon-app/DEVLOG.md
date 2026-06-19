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

## Section 2 — services/ bucket + dcal rename (next)

- Not started. This is the first *worker* block. It touches already-merged calendar code (rename across server + tool + tests + specs) plus creating the `services/` bucket. Sequencing within the block matters: create `services/` (2.1) → move server (2.2/2.3) → rename tool (2.4/2.5) → rename standing specs (2.6) → verify no stale identifiers (2.7). Keep the tree green at block end (one commit).
