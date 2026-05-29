## Context

`ProviderRegistry` (`src/Dmon.Core/Providers/ProviderRegistry.cs`) holds the active provider as `_activeIndex` (default 0) and the active model as `_activeModelId`, in memory only. `SetProvider`/`SetModel` queue pending values; `CommitPendingSwitch()` applies them and returns `ProviderSwitchResult?`. `TurnHandler.RunTurnAsync` originally called the commit at the *end* of the turn. Config is layered via `IConfiguration` in `Program.cs`:

```csharp
builder.Configuration.AddYamlFile(Path.Combine(cwd,  ".dmon", "config.yaml"), optional: true);
builder.Configuration.AddYamlFile(Path.Combine(home, ".dmon", "config.yaml"), optional: true);
```

Two consequences motivated the change: the active selection is lost on restart, and a between-turns selection took effect one turn late. A first implementation persisted to a dedicated `state.yaml` with separate `activeProvider`/`activeModel` keys (committed in Groups 1–2). It was then superseded by the design below: a single `{provider}/{model}` ref persisted to a git-ignored `config.local.yaml` IConfiguration layer.

Note the original layer order adds project first then global, so — since the last `AddYamlFile` wins — **global currently overrides project**, which is backwards.

## Goals / Non-Goals

**Goals:**

- A canonical `ModelRef` (`{provider}/{model}`) primitive in `Dmon.Abstractions`, reusable by later model-handling work.
- Persist/restore the active selection through `config.local.yaml` as a git-ignored, app-managed IConfiguration layer (project scope), reading for free via `IConfiguration`.
- A between-turns selection is used on the very next turn.
- Correct, intuitive layer precedence: global < project < local.

**Non-Goals:**

- Not changing the RPC/protocol surface (`ModelSetCommand` stays `{Provider, ModelId}`); `ModelRef` is the internal/persisted canonical form, parsed/formatted at the boundary.
- Not a global `config.local.yaml` layer (project scope only).
- Not changing mid-turn switch semantics (a mid-turn switch still defers to the next turn).
- Not adding an external YAML library; the `config.local.yaml` writer is app-owned and may rewrite the file wholesale.

## Decisions

### 1. `ModelRef` value type

`public sealed record ModelRef(string Provider, string? Model)` in `Dmon.Abstractions.Providers`.

- **Parse** splits on the **first** `/`: provider = substring before it (must be non-empty), model = substring after it, taken **verbatim** (may contain `/`). A string with no `/` parses to provider-only (`Model == null`). Empty/whitespace input, or an empty provider, is not a valid ref.
- **`ToString()`** → `Model is null ? Provider : $"{Provider}/{Model}"`.
- Provide `static ModelRef? Parse(string?)` (null on invalid) — or `TryParse` — for the read/restore path which must never throw.

### 2. Persist via a git-ignored `config.local.yaml` IConfiguration layer (project scope)

`config.local.yaml` is an **app-managed, git-ignored** config file at `./.dmon/config.local.yaml`. It carries one top-level key today:

```yaml
activeModel: gemini/gemini-3.1-flash-lite
```

- **Read** is free: add `config.local.yaml` to the `IConfiguration` layer stack (`Program.cs`), and the active model is `IConfiguration["activeModel"]` resolved through the layers. No custom read parser. (A user may even set a default `activeModel` in `config.yaml`, overridden by the app-written `config.local.yaml`.)
- **Write** is app code: on a committed switch, write `activeModel: {provider}/{model}` to `./.dmon/config.local.yaml` atomically (temp file + `File.Move(overwrite)`), creating `.dmon` if needed. Because the file is app-owned and git-ignored, a whole-file rewrite is acceptable — no hand-authored comments to preserve. (The writer should still preserve any other top-level keys it finds, so the file can grow into a general local-override layer; a minimal key-preserving rewrite suffices.)
- **`.gitignore`** gets `config.local.yaml` (or `.dmon/config.local.yaml`).

The active-model store therefore reduces to: `ModelRef? Load()` (delegates to `IConfiguration["activeModel"]` → `ModelRef.Parse`) and `Task SaveAsync(ModelRef, ct)` (writes `config.local.yaml`). It is injected with `IConfiguration` and the working directory.

### 3. Fix layer precedence: global < project < local

Reorder `Program.cs` so the last-wins `IConfiguration` semantics give the intuitive precedence:

```csharp
AddYamlFile(home/.dmon/config.yaml,        optional)   // lowest
AddYamlFile(cwd /.dmon/config.yaml,        optional)   // project overrides global
AddYamlFile(cwd /.dmon/config.local.yaml,  optional)   // local overrides both (highest)
```

This changes resolution for *all* config keys (project now beats global). Small blast radius; intentional and called out in the proposal.

### 4. Restore at startup from IConfiguration

`ProviderRegistry` (already injected with the store after Group 2) calls `store.Load()` in its constructor, after adapter validation. `Load()` returns a `ModelRef?` from `IConfiguration["activeModel"]`. If the ref's provider is a configured provider (case-insensitive), set `_activeIndex` to it and `_activeModelId` to `ref.Model`; otherwise keep index 0. Never throws. (Known limitation, unchanged: extension/dynamic providers registered after construction are not restorable here.)

### 5. Save on commit; commit at turn start

Unchanged from the committed Group 2 except the saved shape is now a `ModelRef`:

- `TurnHandler` commits any pending switch at the **start** of `RunTurnAsync` (before resolving the provider client), emitting a single `ProviderSwitchedEvent { EffectiveNextTurn = false }`, then `await store.SaveAsync(new ModelRef(result.ProviderName, emptyToNull(result.ModelId)))`.
- A mid-turn switch defers to the next turn's start (so the in-flight turn finishes on the old provider).

### 6. Supersede the interim `state.yaml`/two-property design

Groups 1–2 shipped a `state.yaml` store with `activeProvider`/`activeModel`. Group 3 (below) replaces it: rename/retarget the file to `config.local.yaml`, collapse the two keys into one `{provider}/{model}` ref, move reads onto `IConfiguration`, and introduce `ModelRef`. Obsolete `state.yaml` parsing/tests are removed.

## Risks / Trade-offs

- **Risk: precedence reorder changes existing config resolution** (project now overrides global). *Mitigation:* intended; the previous order was the bug. Existing tests/specs that assumed global-wins (if any) are updated.
- **Risk: `config.local.yaml` rewrite clobbers other keys a user added.** *Mitigation:* the writer preserves unrecognised top-level keys (minimal key-preserving rewrite); the file is git-ignored and app-managed by intent.
- **Risk: `IConfiguration` is built once at startup, so a same-session write isn't re-read.** *Mitigation:* the registry is the in-session source of truth after a switch; `IConfiguration` is only consulted at startup. No reload needed.
- **Risk: `ModelRef` parsing of provider-owned ids with slashes.** *Mitigation:* split on the *first* `/` only; everything after is opaque. Covered by tests (`ollama/deepseek/deepseek-v4-pro`).

## Migration Plan

Revise the in-flight (unmerged) `persist-active-model`. Group 3 adopts `ModelRef` + `config.local.yaml`; Group 4 verifies + archives. Branch `change/persist-active-model`.

Rollback: revert the Group 3 commit to fall back to the `state.yaml`/two-property form; revert all to drop persistence.

## Open Questions

None. `ModelRef.Model` is nullable to represent a provider-only ref; the writer is key-preserving; precedence is global < project < local.
