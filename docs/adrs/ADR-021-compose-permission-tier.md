# ADR-021: The `compose` Permission Tier — Gating Agent Self-Modification of the Composition Root

**Date:** 2026-06-13
**Status:** Proposed
**Amends:** ADR-006 — adds an apex `compose` tier and **replaces** its "Extension loading" subsection (whose runtime `extension.load` trigger ADR-019 already removed).
**Closes:** ADR-019 Open Question D.
**Builds on:** ADR-019 (composition-root hosting), ADR-020 (agent definitions).
**Reuses:** ADR-002 (`IChatClient` permission middleware), ADR-003 (`tool.confirmRequest`/`risk`), ADR-014 (park-while-detached).

## Context

ADR-019 makes composition **code**: `Dmon.cs` (and, per ADR-020, each `.dmon/agents/<name>.cs`) declares the core's `#:package` set and builder wiring, and the SDK compiles it **into the core process**. ADR-019's trust argument is "you committed `Dmon.cs` and its packages, as for any dotnet project" — sound for a *human* author. It breaks when the author is the **agent**: dmon is a coding agent that can edit files in its own working directory, including its own composition root. The thing being granted in-process trust and the thing deciding to grant it become the same actor.

The danger is **not** "the agent can execute code" — an agent with the `bash`/`network` tiers already can (`curl … | sh`). The new thing ADR-019 introduces is **where** that code runs. Code pulled via a `#:package` line or written into `Dmon.cs` is compiled into the core and becomes part of the *trusted composition* — it sits **below** the ADR-006 permission layer, alongside the `IChatClient` pipeline that gates everything else, and can therefore **redefine the gates**. Self-modifying the composition root is the agent promoting itself from *gated tool-caller* to *trusted host that defines the gates*. That privilege-boundary crossing is what must be gated, and `bash`-in-a-tier is not it.

ADR-019 removed ADR-006's elaborate runtime extension-load gate (nuspec fetch + LLM source analysis + confirm). But the **risk that gate guarded — third-party code entering the trusted composition — survives**; it merely relocated from a runtime `extension.load` call to a `#:package` line in a file. The gate must follow the risk to its new home.

## Decision

1. **Add an apex `compose` tier to ADR-006.** Above `read` / `write` / `bash` / `network`, `compose` gates any **agent-initiated** change to a composition root — the root `Dmon.cs` or an ADR-020 `.dmon/agents/*.cs` — that would be compiled into the core. It is the highest tier because it is the only one that can rewrite the host enforcing all the others. This subsection **replaces** ADR-006's "Extension loading" tier.

2. **The gate fires at the build/reload chokepoint, not the keystroke.** Editing `Dmon.cs` is inert until `/reload` (ADR-019 D8) compiles and runs it; a reload that would compile a composition **differing from the last human-approved one** triggers the `compose` gate. The prompt itemizes the diff and, prominently, every **new or changed `#:package`** entry. Writing the file still passes the ordinary `write` tier; `compose` gates the *activation*. Defense in depth: even an edit via an already-approved write path still meets the `compose` gate at reload.

3. **Two sub-risks, one tier.**
   - **A new/changed `#:package`** is third-party code entering the trusted composition — the relocated ADR-006 supply-chain risk. It is approved by **exact pin** (`packageId@x.y.z`, never a floating range) so what is approved is what is restored; the approval key mirrors the old `extensions.allow` format. ADR-019 deliberately removed the LLM source-analysis pipeline; the gate is **explicit human authorization + pinning**, not automated source analysis.
   - **Hand-written C# / builder wiring** is the agent's own code, but it is still host code below the permission layer, so it gets the apex `compose` confirm — a notch above an ordinary `write`.

4. **`compose` gates the *agent-initiated reload action*, by its initiator — not by attributing the file edit.** The actor is known **by construction** at the reload, so there is no need to attribute a file change across the reload-restart: an agent recompile is an **agent tool call** (gated); a human's `/reload` is a **console command** — their own authority, exactly "you committed the project" — and is not prompted. Checking at the *action*, before the rebuild, is what makes the initiator unambiguous. The tier targets the agent changing its own composition; the fully-autonomous case — agent edits *and* agent reloads with no human in the loop — is where the gate is load-bearing (Decision 6). Where an initiator is genuinely ambiguous, the gate **fails toward prompting** (over-asking a human beats silently activating agent-authored host code).

5. **Persistence is deliberately narrower than other tiers.**
   - **Never suppressible globally.** A tier that can redefine the gates must not be blanket-trusted across all projects; there is no `~/.dmon` "allow compose globally."
   - **Package approvals** are storable **per-project** keyed by exact pin (`permissions.compose.packages.allow: ["Acme.Ext@2.1.0"]`); a version bump is a new key and a new prompt (as the old extension gate did).
   - **Self-edit** (non-package C# changes) defaults to a per-reload confirm and may be pre-authorized **per-project only** (an explicit opt-in: "this is a project where the agent manages its own composition"); never global.
   - **Deny** abandons the reload — the composition stays at the last approved build; the agent is told.

6. **No silent `compose` without a human — reuse park-while-detached.** In a detached/headless or remote session (ADR-012) a `compose` request has no one to answer it. It **parks** using the gateway's existing park-while-detached machinery (ADR-014 / the `remote-session-gateway` spec): delivered to a human on attach, abandoned with the turn if the handler is reaped first. For deliberately-autonomous deployments, a human may pre-set a **pinned package allowlist** in project settings; a `compose` request inside that allowlist proceeds, anything outside it parks or is denied. `compose` is **never auto-approved** in the absence of a human or an explicit prior pin.

7. **Reuse the existing enforcement; add no wire surface.** The gate rides the ADR-002 `IChatClient` permission middleware and is surfaced as an ADR-003 `tool.confirmRequest` with **`risk: high`** — no new `risk` enum value, so ADR-003 is untouched. What distinguishes `compose` from other `high`-risk operations is behavioural, not a new code: the reload chokepoint, the composition diff in the prompt, the exact-pin package key, and the never-global / no-silent-headless rules above.

## Consequences

- **The escalation path is closed without resurrecting the runtime loader.** The agent can still *propose* composition changes (it is a coding agent); it cannot *activate* them into the trusted core without a human authorization at reload — or, headless, a pre-pinned allowlist. The riskiest ADR-019 surface gains a gate while ADR-019's subtraction (no `Dotnet.Script`, no runtime NuGet downloader, no ALC reflection-load) stands.
- **The supply-chain check moves from analysis to authorization + pinning.** Weaker than the old LLM source pass in one sense (no automated inspection), stronger in another (exact-pin, what-you-approve-is-what-runs, and the reviewer is a human looking at a named, versioned package rather than trusting an analysis verdict). Consistent with ADR-019's deliberate removal of the analysis pipeline.
- **ADR-020 agents are covered uniformly.** Every composition root — root and named agents — passes the same `compose` gate; no per-agent exception.
- **A new settings shape** (`permissions.compose.packages.allow`, a per-project self-edit opt-in) and the removal of `permissions.extensions` from ADR-006. The denylist (ADR-006) may grow a package-deny form (Open Question A).
- **Friction is concentrated where it belongs.** Routine tool calls are unaffected; the one place the agent meets a hard, non-globally-suppressible gate is when it tries to rewrite what it is.

## Alternatives

- **Gate the file write only, not the reload.** Rejected: the write is inert and may go through an approved write-path; the activation (compile-into-host) is the real event, and gating it catches every route to it.
- **Treat self-written C# as an ordinary `write` (only new packages get the strong gate).** Considered (the "bash-equivalent" view) and rejected: compiled-in C# is host code below the permission layer and can redefine the gates, which sandboxed bash cannot — it warrants the apex tier even with no third party involved.
- **Resurrect the LLM source-analysis pipeline at `#:package` time.** Rejected: ADR-019 deliberately removed it; re-adding it rebuilds the machinery ADR-019 subtracted. Authorization + exact pinning is the lighter successor.
- **Allow a global "trust compose" approval.** Rejected: blanket-trusting the one tier that can redefine all others defeats the gate.
- **Introduce a `critical` `risk` value for `compose`.** Rejected for V1: it would amend ADR-003's wire enum for no behavioural gain; `risk: high` plus the behavioural rules suffice.

## Open Questions

- **A. Package denylist.** Whether ADR-006's non-negotiable denylist gains a package-deny form (e.g. known-bad `packageId` patterns checked before any `compose` approval), mirroring the bash denylist.
- **B. Pin vs lockfile.** Whether "exact pin" is enforced purely by the approval key or also by a committed restore lock, so a registry cannot serve different bytes for an approved `packageId@version`.
- **C. ~~Attributing an edit to agent vs human at reload.~~** *Resolved by Decision 4 — gate the reload **action** by its known initiator (agent tool call vs human console command), not the file edit; fail toward prompting when ambiguous.*
- **D. `dmon init` and first authoring.** Confirming the human-scaffold path (ADR-019 D5) and a human's first `Dmon.cs` never trip the `compose` gate, while every subsequent agent-initiated change does.
- **E. Autonomous mid-session composition.** Whether a pinned-allowlist autonomous agent may compose mid-session at all, or only at session start (interacts with ADR-020 D4's per-session immutability).

## Relationship to other ADRs

- **ADR-006** — amended: the apex `compose` tier is added and the "Extension loading" subsection is replaced. Tiers become read / write / bash / network / **compose**; the gate still sits in the ADR-002 `IChatClient` middleware.
- **ADR-019** — closes its Open Question D and supplies the "permission-model revision" its ADR-006 reframe defers to. The hosting model is unchanged; this only governs *who* may change a composition root and *when* it takes effect.
- **ADR-020** — every `.dmon/agents/*.cs` is a composition root under the same gate; per-session agent immutability (ADR-020 D4) limits *when* composition changes can occur, and `compose` limits *whether* the agent may make them.
- **ADR-003** — untouched: `compose` reuses `tool.confirmRequest` with `risk: high`; no new message or enum value.
- **ADR-012 / ADR-014** — headless/remote `compose` requests park via the existing park-while-detached machinery; never auto-resolved.
- **ADR-002** — the gate is enforced in the existing permission middleware before tool dispatch, the same chokepoint all tiers use.
