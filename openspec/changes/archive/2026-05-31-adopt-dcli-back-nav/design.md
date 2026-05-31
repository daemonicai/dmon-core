## Context

dmon's terminal host renders provider-setup wizard steps over `dcli`'s awaitable dialog methods. dmon is pinned to `Dcli 0.2.0-rc.2`. Two dcli dialog capabilities post-date that pin:

- `InputRequest.AllowBack` — being added by dcli's `back-nav-input` change (a parallel proposal in the sibling `dcli` repo). Not yet published.
- `Terminal.MultiSelectAsync` (with `MultiSelectRequest`, back via the `[` key) — already shipped in dcli `0.2.0-rc.3` (`api-ergonomics-pass-2`).

Today the consequences in `ConsoleEventHandler` are: the provider-setup API-key step (`RenderTextInputAsync`) is the only wizard step that cannot be backed out of, and `RenderChooseManyAsync` renders a `ChooseManyStep` as a single-select stand-in. The `DialogOutcome.Back → WizardAnswerOutcome.Back` mapping is already present defensively in `RenderTextInputAsync`, and the wizard wire format already carries comma-separated indices and the `Back` outcome — so the Core-side protocol needs no change.

## Goals / Non-Goals

**Goals:**
- Adopt the dcli release containing `InputRequest.AllowBack` and activate text-input back-navigation on the API-key/free-text wizard step.
- Render `ChooseManyStep` as a true multi-select prompt via `MultiSelectAsync`.
- Keep the change to the terminal renderer and the dependency bump only.

**Non-Goals:**
- Implementing `back-nav-input` in dcli (separate repo).
- Any change to `WizardEngine`, the RPC wizard contract, or `Dmon.Protocol` step types.
- Introducing a `ChooseManyStep` producer (no built-in factory emits one today; this change only fixes how the renderer would present one).
- Turn persistence or any other dcli-version-coupled work.

## Decisions

**D1 — Bundle text-input back-nav and multiselect into one adoption change.** The multiselect primitive needs only the already-published rc.3, while the text-input flip is gated on the unpublished `back-nav-input` release. They are bundled because both are "adopt the newer dcli dialog surface" and share the dependency bump, keeping a single version move rather than two. _Alternative considered:_ ship multiselect now against rc.3 and the text-input flip later — rejected by the user in favour of one coherent change. _Consequence:_ the whole change is gated (see D2).

**D2 — Treat the change as GATED on the dcli `back-nav-input` release.** Implementation cannot complete until dcli publishes a release containing `InputRequest.AllowBack` to the package source. The exact target version is unknown at proposal time (it follows rc.3), so the version string is deferred to an implementation task rather than hard-coded in the spec. _Mitigation:_ the apply workflow must verify the published version exposes `InputRequest.AllowBack` before bumping; if it is not yet published, the change stops and waits.

**D3 — Text-input Back trigger is Backspace-on-empty, owned by dcli.** The renderer only sets `AllowBack = true`; the trigger semantics (Backspace returns `Back` when the field is empty, regardless of prior edits) live in dcli's `InputDialog`. dmon does not reimplement the key handling. This keeps the substrate decision in the substrate, per the "fix dcli, don't work around" stance. The existing `DialogOutcome.Back → WizardAnswerOutcome.Back` mapping in `RenderTextInputAsync` means activation is a one-line flag flip.

**D4 — Multi-select Back uses dcli's `[` key, not Backspace.** `MultiSelectRequest.AllowBack` binds `[` (Backspace is ambiguous with Space-toggle in a multi-select list). The renderer maps the resulting `DialogOutcome.Back` to `WizardAnswerOutcome.Back` exactly as the select/input paths do. The choose-many answer remains the existing comma-separated index encoding, so no Core decode change is needed.

## Risks / Trade-offs

- **[Gated on an external release]** → The change can be proposed and reviewed now but cannot pass its gates until dcli publishes. The apply workflow halts at the version-bump task until the dependency is available; the spec carries no hard-coded version to avoid churn.
- **[No live `ChooseManyStep` consumer]** → The multi-select rendering cannot be exercised end-to-end through a real wizard flow today; it is covered by renderer unit tests against `FakeTerminal` only. Accepted: the user wants the substrate adopted ahead of the first consumer.
- **[Backspace-on-empty surprise]** → A user clearing a field then pressing Backspace again is taken back a step. This is intended (Backspace-on-empty has no delete role) and is dcli's documented behaviour; dmon does not alter it.

## Open Questions

_None._ The dcli target version is intentionally left as an implementation-time task (D2), not an open question.
