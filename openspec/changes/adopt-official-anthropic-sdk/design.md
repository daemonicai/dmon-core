## Context

`Dmon.Core` dies on the first turn against the Anthropic provider:

```
[Error] Method not found: 'System.String Microsoft.Extensions.AI.HostedMcpServerTool.get_AuthorizationToken()'.
```

This is a binary (ABI) incompatibility, not a source bug. Investigation of the NuGet cache and registry established:

- `Anthropic.SDK 5.10.0` — the **community** SDK by `tghamm`, currently referenced by `src/Dmon.Providers` — is the latest release and is compiled against `Microsoft.Extensions.AI.Abstractions 10.3.0`. The `HostedMcpServerTool.AuthorizationToken` getter exists in `10.3.0`, was **removed** in `10.4.1`, and is absent in `10.5.1`/`10.6.0`.
- The solution pins the `Microsoft.Extensions.AI` family to `10.6.0`, so the abstractions assembly resolves transitively to `10.6.0`. At runtime `Anthropic.SDK`'s IL calls `get_AuthorizationToken()` on the `10.6.0` `HostedMcpServerTool`, which no longer defines it → `MissingMethodException`.
- The **official** Anthropic-maintained SDK is the `Anthropic` NuGet package (`anthropics/anthropic-sdk-csharp`). Latest `12.24.1` depends on `Microsoft.Extensions.AI.Abstractions 10.5.1`, is actively maintained, and `AnthropicClient` exposes `AsChatClient()` returning an `IChatClient`.

Two ways out: (a) downgrade the M.E.AI family to `10.3.0` to match the community SDK, or (b) adopt the official `Anthropic` package and align the family to `10.5.1`. This change takes (b): it fixes the crash *and* moves off the stale community package onto the first-party, maintained client — better long-term and only a small source delta.

## Goals / Non-Goals

**Goals:**

- The agent core completes a turn against the Anthropic provider without faulting on a missing M.E.AI member.
- `src/Dmon.Providers` uses the official `Anthropic` package; the `IChatClient` is obtained via `AnthropicClient.AsChatClient()` (ADR-001).
- The `Microsoft.Extensions.AI` family resolves to `10.5.1` — the version the official SDK is built against — so the whole solution is ABI-consistent.
- The Anthropic provider stays functionally equivalent: custom base URL, streaming, tool calls, reasoning parameter.

**Non-Goals:**

- Not introducing central package management (`Directory.Packages.props`); the per-csproj pin convention is kept.
- No change to model listing (raw HTTP), the wizard, capability mapping, or `CapabilitiesDecorator`.
- Not touching the OpenAI / Gemini / Ollama / omlx providers beyond the shared M.E.AI version bump they already carry.
- Not pinning to M.E.AI `10.6.0` — the official SDK targets `10.5.1`; staying at the SDK's target avoids re-introducing a forward-ABI gap.

## Decisions

### 1. Adopt the official `Anthropic` package; remove `Anthropic.SDK`

In `src/Dmon.Providers/Dmon.Providers.csproj`: drop `Anthropic.SDK` `5.10.0`, add `Anthropic` `12.24.1`.

### 2. Align the whole M.E.AI family to 10.5.1, in lockstep

The six references move `10.6.0` → `10.5.1`:

| Project | Package |
|---------|---------|
| `src/Dmon.Core` | `Microsoft.Extensions.AI` |
| `src/Dmon.BuiltinTools` | `Microsoft.Extensions.AI` |
| `src/Dmon.Abstractions` | `Microsoft.Extensions.AI` |
| `src/Dmon.Extensions` | `Microsoft.Extensions.AI` |
| `src/Dmon.Providers` | `Microsoft.Extensions.AI.OpenAI` |
| `extensions/Dmon.Extensions.Omlx` | `Microsoft.Extensions.AI.OpenAI` |

All M.E.AI packages move together so abstractions resolves to exactly `10.5.1` (the official SDK's target). Mixing `10.6.0` meta-packages with the SDK's `10.5.1` would let NuGet unify upward to `10.6.0` and re-expose a forward-ABI gap — the very class of failure we are fixing.

### 3. Rewrite `AnthropicProviderFactory.CreateAsync` for the official API

Current (community):

```csharp
using Anthropic.SDK;
...
AnthropicClient client = string.IsNullOrWhiteSpace(apiKey)
    ? new AnthropicClient()
    : new AnthropicClient(apiKey);
if (config.BaseUrl is not null)
    client.ApiUrlFormat = $"{config.BaseUrl.TrimEnd('/')}/{{0}}/{{1}}";
ChatClientCapabilities caps = GetCapabilities(modelId);
return ValueTask.FromResult<IChatClient>(new CapabilitiesDecorator(client.Messages, caps));
```

Target (official): `using Anthropic;`, construct the official `AnthropicClient`, and obtain the `IChatClient` via `client.AsChatClient()` rather than `client.Messages`. The structure (api-key-or-default, base-url, `CapabilitiesDecorator`, `ValueTask.FromResult`) is preserved.

**Worker investigation points (resolve against the installed `Anthropic 12.24.1` API):**

1. **Constructor.** The official `AnthropicClient` constructor signature for an explicit API key vs. environment default (`ANTHROPIC_API_KEY`). Preserve today's behaviour: explicit key when provided, otherwise the no-arg/env path.
2. **Base URL.** The community `ApiUrlFormat` property has **no** official equivalent — the official client takes a base URL / environment via client options (e.g. an options object passed to the constructor). Map `config.BaseUrl` onto that option. If the official client cannot express a custom base URL at all, that is a spec-relevant gap — stop and surface it rather than silently dropping `config.BaseUrl`.
3. **`AsIChatClient(modelId)`.** The turn loop (`TurnHandler` line ~221) builds `ChatOptions options = new();` and does **not** set `options.ModelId`; every provider factory bakes the configured model into its client as the default (OpenAI: `new ChatClient(modelId, …)`; Gemini: `GeminiClientOptions.ModelId`). So the Anthropic factory MUST pass the configured model id as the default — `client.AsIChatClient(modelId)` — mirroring OpenAI. A parameterless `AsIChatClient()` leaves the client with no default model and makes turns throw `Model ID must be specified either in ChatOptions or as the default for the client.` Keep the `CapabilitiesDecorator(asChatClient, caps)` wrapper.

### 4. Record the version coupling

Add a one-line comment near the `Anthropic` and `Microsoft.Extensions.AI*` references noting that the M.E.AI family is pinned to the `Microsoft.Extensions.AI.Abstractions` version the `Anthropic` package targets (`10.5.1`), and must be re-validated when the `Anthropic` package is bumped. Terse — per the project's "comment only non-obvious constraints" rule.

## Risks / Trade-offs

- **Risk: the official `AnthropicClient` cannot set a custom base URL.** `config.BaseUrl` is a supported provider option today. *Mitigation:* worker confirms the official options API; if genuinely unsupported, stop and surface it (don't drop the feature silently).
- **Risk: behavioural drift in streaming / tool calls / reasoning between the two SDKs' `IChatClient` adapters.** The provider has no offline unit coverage. *Mitigation:* mandatory manual smoke against a live key — a streamed turn, a tool call, and a reasoning request — before archive.
- **Risk: another dependency transitively requires M.E.AI `10.6.0+`.** *Mitigation:* the `make build` gate (with `TreatWarningsAsErrors`) fails loudly on a downgrade conflict; reassess rather than force-pin.
- **Risk: `GeminiDotnet.Extensions.AI 0.25.0` / `Microsoft.Extensions.AI.OpenAI 10.5.1` incompatibility with abstractions `10.5.1`.** *Mitigation:* build + full test suite is the verification; both are lockstep/older and expected to be fine.

## Migration Plan

Single capability, two task groups: (1) dependency swap + factory rewrite + gates + reviewer audit + commit; (2) manual Anthropic smoke (HITL), final gates, archive. Branch `change/adopt-official-anthropic-sdk` off `main`.

Rollback: revert the `.csproj` and `AnthropicProviderFactory.cs` edits; the solution returns to `Anthropic.SDK 5.10.0` + M.E.AI `10.6.0` (and the crash returns).

## Open Questions

1. Does the official `AnthropicClient` support a custom base URL (for `config.BaseUrl`)? *Tentative:* yes, via a client-options object — worker to confirm against the installed API. If not, surface as a scope/spec gap before proceeding.
