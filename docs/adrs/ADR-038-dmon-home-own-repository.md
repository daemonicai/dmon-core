# ADR-038: `dmon-home` Moves to Its Own Repository

**Date:** 2026-09-08
**Status:** Proposed
**Supersedes:** ADR-037 **Decision 1 only** — the `home/` top-level bucket. `dmon-home` ceases to be a member of this monorepo. Consequentially this **reverts ADR-037 D1's amendments**: `home/` leaves ADR-025 D2's bucket set and ADR-028 D1's bucket membership, both of which return to their pre-ADR-037 text.
**Retains:** ADR-037 **Decisions 2, 3, 4 and 5 in full** — gateway-client-not-stdio-host (D2), the supersession of `dmonium` and the `dmon-home` / `ai.daemonic.dmon-home` / `DmonHomeApp` naming (D3), the speech hosting decision (D4, itself pending a separate reversal — see Decision 5 below), and the Apple-Silicon-only constraint (D5). None of those decisions depends on which repository the code sits in.
**Builds on:** ADR-012 (the `gw` control-frame sub-protocol, which becomes the cross-repo contract seam), ADR-025 (bucket set and path-filtered CI), ADR-028 (bucket membership, `daemon/Daemon.App`), ADR-035 (release families — `dmon-home`'s prospective artifact leaves this repo's matrix).

## Context

ADR-037 D1 placed `dmon-home` in a new `home/` bucket inside `dmon-core`, and gave its reasons
against the alternatives available *at the time* — all of which were other buckets in the same
repository. A separate repository was not among the options it weighed.

The reasons D1 gave for `home/` over `frontends/` or `daemon/` were sound and remain sound; they
are arguments about **not filing a Swift client product beside .NET protocol surfaces or beside
the product it replaces**. Every one of them is satisfied *more* completely by a separate
repository than by a sibling bucket. D1 is superseded not because it was wrong but because it
answered a narrower question than the one now being asked.

What has changed since:

- **The product is real and its foundations are proven.** `dmon-home-foundations` shipped
  (#109), and all four live verifications in `home/VERIFICATION-NOTES.md` have now been run and
  passed (#111) — including the two that no test could close: the Keychain round trip and the
  two-language auth contract against `Dmon.Network`. The seam between the two languages is
  demonstrated, not assumed. **This is the precondition that makes a split safe**, and it did not
  exist when D1 was written.
- **`home/` shares nothing but the wire.** It carries no .NET projects, no `.slnx`, no
  `ProjectReference` into any C# project, and no build-time dependency on anything in this
  repository. Its only coupling to `dmon-core` is the ADR-012 `gw` sub-protocol, spoken over a
  socket — a contract that is already versioned and already checked at connect.
- **The monorepo's own mechanisms treat it as foreign.** `Everything.slnx` cannot contain it;
  ADR-025 D9's "core ⇒ all" CI rule explicitly does not apply to it; it needs a dedicated macOS
  job with its own path filter, and a second one for the iOS portability gate. `make clean`
  cleans neither of its Swift trees. Each of these is a small accommodation for a member that
  does not fit the repository's shape.

## Decision

1. **`dmon-home` lives in its own repository, `daemonicai/dmon-home`.** The `home/` directory is
   removed from `dmon-core` and its contents — `PRD.md`, `project.yml`, the `DmonHomeApp` app
   target, `Sources/`, `Tests/`, `TOOLCHAIN-NOTES.md` and `VERIFICATION-NOTES.md` — become that
   repository's root. `home/` leaves ADR-025 D2's bucket set and ADR-028 D1's bucket membership;
   both revert to their pre-ADR-037 text, and ADR-025's rule that a memberless role bucket has no
   directory does the rest. The `dmon-home` and `dmon-home-ios-check` CI jobs, their path
   filters, and the `dmon-home*` `Makefile` targets go with it. **Supersedes ADR-037 D1.**

   `VERIFICATION-NOTES.md` was already written to travel with `home/`; it now does.

   **The repository already exists** (`git@github.com:daemonicai/dmon-home.git`), at a single
   `Initial commit`: a README stub, an OpenSpec root with empty `specs/` and `changes/`, and the
   base OpenSpec skills. So the move is a migration into a scaffolded repository, not a creation.

   Two things follow, and the second is a hazard rather than a detail:
   - The OpenSpec root of Decision 3 exists already and needs populating, not initialising.
   - **`PRD.md` is already there, byte-identical to `home/PRD.md`.** Two copies of the product's
     requirements document now exist in two repositories, and nothing keeps them in step. The
     migration MUST resolve this to a single copy in `dmon-home` — the duplicate is not a
     harmless head start, it is the first thing that will silently diverge, and the ADR-037 D1
     bucket description that names `home/PRD.md` as the bucket's founding member is the reason
     it looks legitimate in both places.

2. **The split happens now; `dmonium`'s retirement follows later, across the repository
   boundary.** ADR-037 D3 retires `daemon/Daemon.App` only once `dmon-home` covers its full
   surface — all seven of `bootstrap()`'s children plus the mlx reasoner, the mlx triage head and
   speech. Today only the network gateway is enabled, so that retirement is far off, and holding
   the split until parity would keep a foreign member in this repository for the entire interval.

   The cost is explicit and must not be lost: **the parity comparison becomes cross-repo.** D3's
   obligation survives the split unchanged, but nothing in either repository's build or CI can
   check it. Therefore:
   - `daemon/Daemon.App` remains a supported, building, shipping member of `daemon/`, and ADR-028
     D1/D2/D6 remain in force for it, exactly as ADR-037 D3 says.
   - The retirement is **`dmon-core`'s change to make**, not the new repository's, because the
     code being deleted lives here.
   - The parity evidence — which of the ten children `dmon-home` actually supervises — lives in
     the new repository. A retirement proposal in `dmon-core` must cite it rather than assert it.

   Recorded because a reader finding `Daemon.App` still shipping long after `dmon-home` left will
   otherwise reasonably conclude the retirement was forgotten.

3. **The three `dmon-home` capability specs move.** `dmon-home`, `dmon-home-gateway-client` and
   `dmon-home-supervision` — created in `openspec/specs/` by the `dmon-home-foundations` archive —
   are removed from `dmon-core` and become the new repository's standing specs under its own
   OpenSpec root. A spec must live where the code it governs lives, or it decays: nobody working
   in `dmon-core` will ever have cause to open them, and OpenSpec's own workflow gives them no
   validation signal here once `home/` is gone.

   `dmon-home-gateway-client` moves **with** the others despite describing a client of *this*
   repository's protocol. The contract it must not violate is ADR-012's, which stays here and is
   the seam of Decision 6; the spec describes the Swift client's obligations, which are the new
   repository's to meet.

4. **`home/`-specific tech-debt notes move with the code they describe.** Of the register's
   entries, those describing Swift supervisor behaviour, the Swift toolchain, and the macOS host's
   UI travel to the new repository with a correspondingly split README index.

   One note **straddles the boundary and must be handled deliberately, not sorted**:
   `provisioning-races-device-store-reload.md` describes a race between a Swift client that
   provisions a credential and a .NET gateway that reloads its device store. Half of its subject
   stays in `dmon-core`. It is the first real test of Decision 6 and, per Decision 2's principle,
   the repository that owns the *fix* owns the note — which is undecided precisely because the fix
   is undecided (client retry versus gateway re-read). **It stays in `dmon-core` until that call
   is made**, since the gateway-side fix is the better-argued of the two and lives here.

5. **The new repository gets its own ADR series, starting at ADR-001.** `dmon-core`'s ADR-037
   remains the historical record of why `dmon-home` exists and what it is; this ADR records why it
   left. Future `dmon-home` architecture decisions are recorded there, not here, and do not
   continue this repository's numbering — two independent series with a stated relationship are
   less error-prone than one series maintained by coordination across two repositories.

   **This has immediate sequencing consequences for the pending speech decision.** The reversal of
   ADR-037 D4 — STT/TTS moving from a Python/mlx sidecar to in-process Swift via
   `soniqo/speech-swift` — is a `dmon-home` decision. It is therefore written in the **new**
   repository as one of its first ADRs, recording that it reverses `dmon-core`'s ADR-037 D4, and
   **should wait for the split rather than landing here first**. Until it is accepted, ADR-037 D4
   stands as written.

6. **The ADR-012 `gw` sub-protocol is the entire cross-repo contract, and stays in `dmon-core`.**
   No other coupling between the two repositories is permitted: no source sharing, no vendored
   copies, no build-time dependency in either direction. The new repository consumes `dmon-core`
   only as a *running gateway* it connects to.

   This is enforceable because the mechanism already exists and is specified: the gateway client
   checks wire-protocol compatibility on connect, and refuses rather than proceeding on a
   mismatch. A protocol change in `dmon-core` therefore surfaces to `dmon-home` as a named refusal
   at runtime, which is the only cross-repo signal a split can offer and the reason the split is
   affordable at all.

   `dmon-core` owns the wire contract. A change to it that breaks the client is `dmon-core`'s to
   announce and the new repository's to absorb.

## Consequences

- **`dmon-core` becomes pure .NET again**, apart from `daemon/Daemon.App`, which remains until its
  retirement. The macOS and iOS Swift CI jobs and their path filters leave, as do four `Makefile`
  targets; ADR-025 D9's "core ⇒ all" rule is unaffected, having never applied to `home/`.
- **`dmon-home` gains its own release cadence.** It has no artifact yet, so ADR-035's package→family
  map is unchanged in fact; the prospective arm64-only app-artifact entry ADR-037 D5 anticipated
  will now appear in the new repository's matrix rather than this one's. ADR-035's decision text
  needs no change.
- **Two repositories must be checked out to work on the voice loop end to end**, and the live
  verifications in `VERIFICATION-NOTES.md` are inherently cross-repo: they require a built `ndmon`
  from `dmon-core` and a built app from `dmon-home`. The notes' setup section already assumes a
  separately built `ndmon`, so this is a documentation change rather than a new burden — but the
  stale-binary trap those notes record gets *worse* across repositories, not better, because
  nothing will rebuild the two together.
- **The provisioning race and any future cross-boundary defect become harder to attribute.** The
  one already found took a live run of both processes to see, and no test in either repository
  could have caught it. After the split there is no build in which both sides exist, so this class
  of defect is discoverable only by running the pair.
- **ADR-037 D3's parity obligation is now unenforceable by any automated means** and rests on the
  record in Decision 2.

## Alternatives considered

- **Keep `home/` and accept the accommodations.** Sound while the product was unproven, and the
  reason D1 was right when written. Rejected now on the grounds that the accommodations are
  permanent and growing (two CI jobs, excluded from the solution and the clean target, exempt from
  the repo-wide CI rule) for a member that shares no build graph with anything here.
- **Split at parity, after `dmonium` is deleted.** Keeps ADR-037 D3's comparison intra-repo, which
  is genuinely simpler. Rejected because parity is nine children away and the interval is long;
  the retirement obligation is recorded in Decision 2 instead.
- **Split when the speech work lands.** Attractive because that change adds `home/`'s first
  external dependency. Rejected as arbitrary — the dependency argument is neutral on repository
  boundary — and because it would mean writing the D4 reversal in this repository and immediately
  moving it (see Decision 5).
- **Keep the specs and ADRs in `dmon-core` as a single normative home.** Rejected: a spec whose
  code is elsewhere has no validation signal and no reader, which is the drift this repository's
  own `tech-debt` register already records instances of.

## Relationship to other ADRs

- **ADR-037** — *Supersedes D1; retains D2–D5.* The bucket is dissolved; every other decision about
  what `dmon-home` **is** survives the move untouched. D4's own pending reversal is now the new
  repository's to write (Decision 5), and D3's retirement obligation is restated cross-repo
  (Decision 2).
- **ADR-025** — *D2 reverts.* `home/` leaves the bucket set, returning D2 to its pre-ADR-037 text.
  D9's path-filtered CI rule and D10's artifact sources are unaffected in fact — `home/` was always
  outside the former and never gained an entry in the latter.
- **ADR-028** — *D1 reverts* (bucket membership). *D2 and D6 are untouched*: `daemon/Daemon.App`
  keeps its placement, name, bundle id and artifact source until the retirement change lands.
- **ADR-012** — *Builds on, unchanged, and load-bearing in a new way.* The `gw` sub-protocol was an
  internal seam between two members of one repository; it is now the sole contract between two
  repositories. No wire-string, frame-shape or transport decision changes — but the cost of
  breaking one rises.
- **ADR-035** — *Unchanged.* `dmon-home` has no release artifact, so nothing leaves the matrix. Its
  prospective arm64-only entry will be created in the new repository.
- **ADR-003 / ADR-034 / ADR-036** — *Unchanged.* Consumed by `dmon-home` exactly as before; none is
  sensitive to repository boundary.

## Open questions

- **Who fixes the provisioning race, and therefore where its note ends up** (Decision 4). The
  gateway-side fix — re-read the device store before rejecting an unknown `keyId` — is the better
  argued but touches the fail-closed auth path; the client-side retry is cheaper but encodes the
  timing assumption. This wants deciding before the split rather than after, because afterwards
  the two candidate fixes live in different repositories.
- **Whether the new repository adopts this repository's OpenSpec apply workflow** (worker /
  reviewer / supervisor agents, DEVLOG conventions) or a lighter one. Decision 3 gives it an
  OpenSpec root; it does not settle the process around it. As it stands the repository has the
  **base** OpenSpec skills only — no `.claude/agents/`, no `CLAUDE.md` — so the Architect-and-
  three-agents loop this repository runs is **not** available there today. Adopting it is a
  scaffolding step, not a decision that happens by default, and it should be settled before the
  first change is applied in the new repository rather than discovered mid-change.
