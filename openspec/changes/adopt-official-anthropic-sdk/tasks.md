## 1. Adopt the official Anthropic SDK and align M.E.AI to 10.5.1

- [x] 1.1 In `src/Dmon.Providers/Dmon.Providers.csproj`, remove the `Anthropic.SDK` `5.10.0` `PackageReference` and add `Anthropic` `12.24.1`.
- [x] 1.2 Change the `Microsoft.Extensions.AI` `Version` `10.6.0` → `10.5.1` in `src/Dmon.Core/Dmon.Core.csproj`, `src/Dmon.BuiltinTools/Dmon.BuiltinTools.csproj`, `src/Dmon.Abstractions/Dmon.Abstractions.csproj`, and `src/Dmon.Extensions/Dmon.Extensions.csproj`.
- [x] 1.3 Change the `Microsoft.Extensions.AI.OpenAI` `Version` `10.6.0` → `10.5.1` in `src/Dmon.Providers/Dmon.Providers.csproj` and `extensions/Dmon.Extensions.Omlx/Dmon.Extensions.Omlx.csproj`.
- [x] 1.4 Add a one-line comment near the `Anthropic` and `Microsoft.Extensions.AI*` references recording the coupling: the M.E.AI family is pinned to the `Microsoft.Extensions.AI.Abstractions` version the `Anthropic` package targets (`10.5.1`); re-validate when bumping the `Anthropic` package.
- [x] 1.5 Rewrite `AnthropicProviderFactory.CreateAsync` in `src/Dmon.Providers/AnthropicProviderFactory.cs` for the official API:
  - `using Anthropic;` (replace `using Anthropic.SDK;`).
  - Construct the official `AnthropicClient` — explicit API key when `apiKey` is provided, otherwise the env/no-arg default (preserve today's behaviour).
  - Map `config.BaseUrl` onto the official client's base-URL option (the community `ApiUrlFormat` property does not exist on the official client). If the official client offers no custom base-URL option, STOP and surface it — do not silently drop `config.BaseUrl`.
  - Obtain the `IChatClient` via `client.AsChatClient()` (replacing `client.Messages`) and keep the `new CapabilitiesDecorator(<chatClient>, caps)` wrapper and `ValueTask.FromResult<IChatClient>(...)` return.
  - Leave `GetAvailableModelsAsync`, the wizard, capability mapping, and `CapabilitiesDecorator` untouched.
- [x] 1.6 Verify dependency resolution: `dotnet list src/Dmon.Providers/Dmon.Providers.csproj package --include-transitive` shows `Anthropic 12.24.1` and `Microsoft.Extensions.AI.Abstractions 10.5.1`, with no NuGet downgrade warning in any project.
- [x] 1.7 Standard gates: `make build` clean (no warnings under `TreatWarningsAsErrors`), `make test` (or `dotnet test -c Release`) green, `openspec validate adopt-official-anthropic-sdk --strict`; reviewer audit; commit.

## 2. Verify the crash is gone + archive

- [x] 2.1 Manual smoke (HITL — provide the recipe and wait for confirmation before ticking): with `ANTHROPIC_API_KEY` set, run `dotnet run --project src/Dmon.Terminal` and confirm against a live key:
  - a streamed turn completes (`turnStart` … `turnEnd`) with **no** `Method not found: ... HostedMcpServerTool.get_AuthorizationToken()` error;
  - a tool call executes end-to-end;
  - a reasoning/thinking request is honoured (model with `SupportsReasoning`).
- [ ] 2.2 (Opportunistic) With the crash unblocked, complete the deferred `markdown-fidelity-pass` task 3.2 visual smoke (nested emphasis + rich link) if convenient — not required for this change.
- [x] 2.3 Standard gates: build, test, `openspec validate adopt-official-anthropic-sdk --strict`.
- [ ] 2.4 Propose `/opsx:archive adopt-official-anthropic-sdk` and wait for user confirmation. Do not archive automatically.
