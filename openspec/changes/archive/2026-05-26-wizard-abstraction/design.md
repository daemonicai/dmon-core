## Context

The `/add-provider` wizard in `Dmon.Terminal` is built from a fixed list of step functions (`WizardSteps.cs`, `WizardRunner.cs`). `AdapterSelectionStep` hardcodes `["anthropic", "openai", "gemini"]`; `AuthConfigStep` hardcodes a per-adapter env-var dictionary and a local/global scope prompt. Only `ModelSelectionStep` is already provider-driven (it calls `IProviderFactory.GetAvailableModelsAsync`).

`IProviderFactory` (see `provider-factories` spec) already exposes `AdapterName`, `GetCapabilities`, `CreateAsync`, and `GetAvailableModelsAsync`. The terminal already has the factory list injected. The result of a completed wizard is delivered to the core via the existing `ProviderConfigureCommand` RPC — that boundary does not change.

Upcoming work (oMLX migration, Ollama provider) needs provider-specific setup flows: optional auth, base-URL overrides, and conditional steps (e.g. Ollama offering a cloud-model variant when a selected local model exceeds available memory). The current hardcoded design cannot express these.

## Goals / Non-Goals

**Goals:**

- Move "what to ask during setup" from the terminal into each provider factory.
- A single, flexible factory contract that supports linear, conditional, and multi-step flows without the terminal knowing provider details.
- Keep the terminal as a thin renderer: it maps step types to prompts, manages back/cancel, and persists the result.
- Migrate Anthropic, OpenAI, and Gemini onto the new contract so the hardcoded lists can be deleted in this change.

**Non-Goals:**

- Driving the wizard over RPC. The wizard remains a local terminal interaction; only the final `ProviderConfigureCommand` crosses the boundary.
- Local/project scope selection in the UI. Setup always writes global config; local overrides are a manual `.dmon/config.yaml` edit.
- Migrating the oMLX provider or adding Ollama (separate follow-on changes).
- Touching `Dmon.Tui` (dead branch).

## Decisions

### Factory as a state machine: `GetNextStepAsync(WizardState)`

The factory exposes one method:

```csharp
ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default);
```

`WizardState` carries the ordered list of already-answered `WizardStep` objects. The factory inspects them, decides what to ask next, and returns either the next step or a `WizardCompletedStep`. This is a near-pure function of state → next step.

**Why over alternatives:**

- *Static step list* (`IReadOnlyList<WizardStep>`): cannot express conditional steps (e.g. "use cloud?" only when the model is large). Rejected.
- *Sequence of answer-dependent delegates* (`WizardSequence` of `Func<answers, step?>`): expresses conditionals but spreads flow logic across closures and complicates back-navigation bookkeeping. The single-method state machine is simpler on the abstraction side and back-navigation is just list truncation.
- *Two methods (auth steps + async model step)*: splits one flow into two contracts and still cannot express the conditional model→cloud branch. Rejected.

Conditional and multi-step flows fall out naturally: the factory reads prior answers (`state.Steps.OfType<TextInputStep>().FirstOrDefault(s => s.Id == "api-key")?.Value`) and branches with plain `if` statements.

### `WizardStep` is a mutable class hierarchy

`WizardStep` is an abstract base class. The question portion (id, prompt, options) is `init`-only; the answer portion is a settable property that flips `IsAnswered`. This matches the project style guide ("`class` for mutable state, `record` for immutable data") — a step is mutable because it gains an answer after rendering.

```
WizardStep (abstract)         Id, Prompt, IsAnswered
├─ ChooseOneStep              Options : IReadOnlyList<WizardOption>, SelectedIndex
├─ ChooseManyStep             Options, MinSelections, SelectedIndices   (included for completeness)
├─ TextInputStep              Default?, Secret, Required, Value
├─ YesNoStep                  Default, Answer
├─ InfoStep                   (no answer; renders and advances)
└─ WizardCompletedStep        Message   (terminal marker, no answer)

WizardOption                  Label (display), Value (stored), Description?
WizardState                   Steps : IReadOnlyList<WizardStep>
```

`WizardOption` separates `Label` from `Value` so the UI can show "Anthropic" / "gemma4:31b (18 GB)" while the factory stores "anthropic" / "gemma4:31b".

### Engine owns provider selection and persistence

Provider selection precedes any factory, so the engine special-cases it: it builds a `ChooseOneStep` from `factories.Select(f => new WizardOption(f.DisplayName, f.AdapterName))`, renders it, resolves the factory from the answer, and only then enters the `GetNextStepAsync` loop. `state.Steps[0]` is the adapter choice; factories ignore it because they only read ids they created.

The engine loop:

```
state = WizardState([ adapterStep ])           // after provider selection
loop:
    step = await factory.GetNextStepAsync(state, ct)
    if step is WizardCompletedStep:
        persist in-flight ProviderConfig to GLOBAL scope
        render step.Message
        return
    outcome = await renderer.RenderAsync(step, ct)   // mutates step's answer
    state = outcome switch:
        Answered => state with Steps = [...state.Steps, step]
        Back     => state with Steps = state.Steps[..^1]   // re-ask previous
        Cancel   => return null
```

Persistence is engine-owned (global scope) so factories never repeat scope logic and the seam between "what config" (factory) and "where persisted" (engine) stays clean. The factory builds the in-flight `ProviderConfig` while collecting answers; the engine writes it.

### Renderer pattern-matches step types onto `InlinePrompt`

The renderer is a `switch` over `WizardStep` subtypes calling the existing `InlinePrompt` helpers (`ChooseAsync`, `ReadLineAsync`, plus a yes/no). `InlinePrompt` already encodes the back signal (`-1`) and cancel (`null`); the renderer translates those into a `StepOutcome` (`Answered` / `Back` / `Cancel`). No new I/O primitives are needed.

### `DefaultEnvVar` retained, scope prompt removed

`DefaultEnvVar` stays on `IProviderFactory` for non-wizard use (credential resolution help, validation). The local/global scope prompt is deleted; setup is global-only.

## Risks / Trade-offs

- **Back-navigation re-invokes `GetNextStepAsync`, which may re-run a network call** (e.g. re-listing models) → Acceptable for V1; the calls are read-only and idempotent. Cache inside the factory later if it proves annoying.
- **Side effects during the flow** → The factory should only mutate the in-flight config and perform read-only provider calls (key validation, model listing) during the loop; disk persistence is deferred to the engine after `WizardCompletedStep`, so back-navigation has nothing committed to unwind.
- **Internal interface break to `IProviderFactory`** → All implementations live in this repo (`Dmon.Providers`) and are migrated within this change; there are no external implementers to coordinate with. The new members have no default implementation by design (every provider must define its setup), so the compiler enforces migration.
- **`ChooseManyStep` has no current consumer** → Included for vocabulary completeness; if it adds noise it can be dropped from this change and added when a provider needs it.

## Migration Plan

1. Add the `Wizard/` types and the two `IProviderFactory` members in `Dmon.Abstractions`.
2. Implement `GetNextStepAsync` + `DisplayName` in all three factories (each reproducing its current behaviour: ask API key, list models, complete).
3. Replace the terminal wizard internals with the engine + renderer; delete the hardcoded adapter list, env-var dictionary, and scope prompt.
4. Verify `/add-provider` end-to-end for each provider against the new flow.

No rollback concern beyond reverting the change; nothing is persisted differently (still `ProviderConfigureCommand` → global config).

## Open Questions

- Should `ChooseManyStep` ship in this change or wait for a real consumer? (Leaning: keep it, it is cheap and rounds out the vocabulary.)
