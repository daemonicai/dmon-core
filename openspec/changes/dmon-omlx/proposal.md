## Why

The `IProviderExtension` interface (provider-extensions change) needs a reference implementation to validate the design end-to-end and give extension authors a concrete example to follow. oMLX — a macOS-native MLX inference server for Apple Silicon — is the ideal first implementation: it is OpenAI-compatible but uses a non-standard auth header, requires an `IsApplicable()` platform check, and has a straightforward lifecycle (`open -a oMLX`, poll for readiness).

## What Changes

- New project `extensions/Dmon.Extensions.Omlx/` in this repo (to be extracted to its own NuGet package later)
- `OmlxProviderExtension` implementing `IProviderExtension` — platform check, server probe, lifecycle management, model listing, capability heuristic
- `OmlxProviderFactory` implementing `IProviderFactory` — custom `HttpClientHandler` that injects `x-api-key` header; wraps OpenAI SDK with configurable base URL
- `OmlxConfig` — port and API key resolved from env vars (`OMLX_BASE_URL`, `OMLX_API_KEY`) or config file, per ADR-005 pattern
- Unit tests covering all `IProviderExtension` members and the capability heuristic
- Project added to `Daemon.slnx`

## Capabilities

### New Capabilities

- `omlx-provider`: The oMLX provider extension — platform check, server lifecycle, model listing, capability heuristic, custom auth factory

### Modified Capabilities

*(none — IProviderExtension and IProviderFactory are unchanged)*

## Impact

- New project under `extensions/` — no changes to existing `src/` projects
- Runtime dependency: oMLX macOS app (https://omlx.ai) — not a NuGet dependency
- NuGet dependencies: `OpenAI` SDK (already used by `Dmon.Providers`), `Microsoft.Extensions.AI` (already in `Dmon.Abstractions`)
- Tagged `dmon-extension` on nuget.org when published externally
