## Why

dmon is pinned to `Dcli 0.2.0-rc.2`, which predates two dialog improvements in dcli: free-text input back-navigation (`InputRequest.AllowBack`, shipping via dcli's `back-nav-input` change) and the `MultiSelectAsync` primitive (already shipped in dcli rc.3). As a result the provider-setup wizard's API-key step is the one step a user cannot back out of, and `ChooseManyStep` is rendered as a single-select fake. Adopting the new dcli dialog surface closes both gaps in one pass.

## What Changes

- Bump the `Dcli` (and `Dcli.Testing`) package reference from `0.2.0-rc.2` to the dcli release that contains `InputRequest.AllowBack`.
- Enable back-navigation on text-input wizard steps: the API-key / free-text step opens its `InputRequest` with `AllowBack = true`, so Backspace on an empty field returns to the previous step. The `DialogOutcome.Back → WizardAnswerOutcome.Back` mapping is already wired defensively in the renderer, so this activation requires no new plumbing.
- Render `ChooseManyStep` as a true multi-select prompt via `Terminal.MultiSelectAsync`, returning the checked indices as the existing comma-separated wire format, replacing the current single-select stand-in. Back-navigation on this step uses dcli's `[` key.

This change is **GATED**: implementation cannot complete until the dcli release containing `InputRequest.AllowBack` is published to the package source. The multiselect portion needs only rc.3 (already published) but is intentionally bundled into this single adoption change.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `terminal-host`: the "Inline wizard prompts" requirement gains back-navigation for free-text/secret steps (`InputRequest { AllowBack = true }`, Backspace-on-empty) and renders choose-many steps via `MultiSelectAsync` with `[`-key back-navigation.
- `provider-setup-wizard`: the "Renderer maps step types to terminal prompts" requirement changes `ChooseManyStep` rendering from a single-selection prompt to a true multi-select prompt that returns multiple selected indices.

## Impact

- **Dependencies:** `Dcli` and `Dcli.Testing` package version bump in `src/Dmon.Terminal/Dmon.Terminal.csproj` and `test/Dmon.Terminal.Tests/Dmon.Terminal.Tests.csproj`. Gated on the dcli `back-nav-input` release.
- **Code:** `ConsoleEventHandler.RenderTextInputAsync` (set `AllowBack = true`) and `ConsoleEventHandler.RenderChooseManyAsync` (switch to `MultiSelectAsync`) in `src/Dmon.Terminal`.
- **Tests:** `Dmon.Terminal.Tests` renderer tests via the existing `FakeTerminal` — new coverage for the text-input step returning `Back` and choose-many returning multiple indices.
- **Out of scope:** the dcli `back-nav-input` implementation itself (separate repo); the Core-side `WizardEngine` / RPC contract (the wire format already supports multiple indices and the `Back` outcome); turn persistence.
