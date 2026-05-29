## Why

`Dmon.Core` crashes on the first turn with `Method not found: 'System.String Microsoft.Extensions.AI.HostedMcpServerTool.get_AuthorizationToken()'`, making the agent unusable with the Anthropic provider. The root cause is a binary-incompatible version mismatch: the bundled `Anthropic.SDK 5.10.0` (the community SDK by `tghamm`, latest release) is compiled against `Microsoft.Extensions.AI.Abstractions 10.3.0`, where `HostedMcpServerTool.AuthorizationToken` exists, but the solution pins the `Microsoft.Extensions.AI` family to `10.6.0`, which removed that getter in `10.4.1`.

Rather than downgrade the M.E.AI family to the community SDK's stale `10.3.0`, adopt the **official** Anthropic-maintained SDK — the `Anthropic` NuGet package (open source at `anthropics/anthropic-sdk-csharp`). It is actively versioned (latest `12.24.1`), tracks `Microsoft.Extensions.AI.Abstractions 10.5.1`, and exposes `AnthropicClient.AsChatClient()` returning an `IChatClient` (ADR-001 compliant). Aligning the M.E.AI family to `10.5.1` — the version the official SDK is built against — removes the ABI crash and puts the project on the maintained, first-party client.

## What Changes

- **Replace the Anthropic client package** in `src/Dmon.Providers`: remove `Anthropic.SDK` `5.10.0`; add `Anthropic` `12.24.1`.
- **Pin the `Microsoft.Extensions.AI` family to `10.5.1`** (from `10.6.0`) across `src/Dmon.Core`, `src/Dmon.BuiltinTools`, `src/Dmon.Abstractions`, `src/Dmon.Extensions` (`Microsoft.Extensions.AI`) and `src/Dmon.Providers`, `extensions/Dmon.Extensions.Omlx` (`Microsoft.Extensions.AI.OpenAI`), matching the official SDK's abstractions version so the whole solution is ABI-consistent.
- **Rewrite `AnthropicProviderFactory.CreateAsync`** to use the official API: `using Anthropic`, construct the official `AnthropicClient`, obtain the `IChatClient` via `.AsChatClient()` (replacing `client.Messages`), and map `config.BaseUrl` onto the official client's base-URL option (the community `ApiUrlFormat` property does not exist on the official client).
- **No change** to the raw-HTTP model listing (`GetAvailableModelsAsync`), the wizard flow, capability mapping, or `CapabilitiesDecorator` — those are SDK-independent.
- Record the M.E.AI/Anthropic version coupling so the family is not bumped past what the official SDK targets without re-validating.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `agent-core` — adds a requirement that the core's runtime dependency closure be ABI-consistent with the active provider SDK, so a turn can be initiated without a runtime `MissingMethodException`.

## Impact

- **Dependencies:** `src/Dmon.Providers` swaps `Anthropic.SDK 5.10.0` → `Anthropic 12.24.1`; the `Microsoft.Extensions.AI` family moves `10.6.0` → `10.5.1` across six `PackageReference` entries (transitively aligning `Microsoft.Extensions.AI.Abstractions` to `10.5.1`).
- **Code:** `AnthropicProviderFactory.cs` only — the `using` and `CreateAsync` body. No other source files change.
- **Runtime:** unblocks the `Dmon.Core` MCP/M.E.AI startup/turn crash, which several changes' manual-smoke verification tasks (e.g. `markdown-fidelity-pass` task 3.2) were gated on.
- **Behaviour:** the Anthropic provider must remain functionally equivalent — streaming turns, tool calling, and reasoning/thinking parameter mapping — verified by a manual smoke against a live key, since the provider has no offline unit coverage.
- **Forward note:** the M.E.AI family tracks whatever `Microsoft.Extensions.AI.Abstractions` version the official `Anthropic` package targets (`10.5.1` today). Re-evaluate the pin when bumping the `Anthropic` package.
