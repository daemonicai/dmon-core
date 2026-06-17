# Dmon.Desktop

The Avalonia desktop host for dmon — a thin GUI frontend that spawns a **local**
`dmoncore` process over JSONL/stdio through `Dmon.Runtime` and drives it exactly
as the terminal host does ("TUI-with-pixels"). It shares all agent behaviour with
`Dmon.Terminal` and **zero UI code**.

The first cut is single-window / single-session parity with the TUI: it renders
the conversation parts model with live streaming, answers tool-confirmation and
UI-input requests with modal dialogs, and supports reloading the core between
turns. ReactiveUI MVVM with routing from the start (`SessionViewModel : IScreen`),
themed with [`Pipboy.Avalonia`](https://github.com/NeverMorewd/Pipboy.Avalonia);
assistant markdown is rendered with `Markdown.Avalonia`.

## Prerequisites

- **.NET 10 SDK.**
- **A populated `dmoncore` NuGet cache.** Like the `dmon` dotnet tool, the desktop
  host resolves the `dmoncore` engine at runtime from the global NuGet cache via
  `ICoreLauncher` (see below) — it does **not** bundle a core. On a dev machine the
  cache is populated by a normal restore/build of this repo; if the core cannot be
  resolved, launch fails into the host's fault state with the acquisition error.
- **A configured provider.** Provider credentials are read by the core from
  environment variables (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`)
  or a config file, exactly as for the terminal host. With no provider configured
  the core reports setup-required.

## Running

From the repository root:

```
dotnet run --project frontends/Dmon.Desktop
```

The window opens in a **boot** state and becomes interactive once the core has
started and passed the runtime `agentReady` protocol-compat gate.

To point the host at a specific core build instead of the cache-resolved one
(useful during core development), pass `--core-path`:

```
dotnet run --project frontends/Dmon.Desktop -- --core-path /path/to/dmoncore
```

## Core acquisition (runtime-resolve)

The host acquires the engine through the **same runtime-resolve-from-NuGet-cache**
mechanism the `dmon` dotnet tool uses (`Dmon.Runtime`'s `ICoreLauncher`). There is
no desktop-specific bundling, pinning, or distribution step. This keeps the host
inside the existing distribution model (ADR-011/019/024/025).

## Deferred: installable artifact

Producing a **self-contained, installable desktop artifact that ships its own
pinned core** is out of scope for this first cut. The current build assumes a
populated cache (the dev-machine assumption); how a shippable desktop artifact
acquires/pins the core (bundle vs. resolve-at-runtime) is an open question for a
later release-focused change. Multi-session / multi-tab and the V1.5+ desktop
affordances (visual diff preview, side-by-side tool panels, session-graph view,
extension browser) are likewise deferred.

Licensed under the [Mozilla Public License 2.0](https://www.mozilla.org/en-US/MPL/2.0/).
