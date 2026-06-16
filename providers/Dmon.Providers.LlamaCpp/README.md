# Dmon.Extensions.LlamaCpp

Managed local [llama.cpp](https://github.com/ggml-org/llama.cpp) provider for the [dmon](https://github.com/daemonicai/dmon-core) coding agent.

This extension spawns and owns a `llama-server` subprocess, exposes it as a `Microsoft.Extensions.AI` `IChatClient`, and gates tool-calling capability via a startup probe — so the agent knows at session start whether the chosen model and its chat template actually support tool calls.

---

## Prerequisites

`llama-server` must be on your `PATH`. This package does **not** bundle or download the binary.

| Platform | Install command |
|----------|----------------|
| macOS | `brew install llama.cpp` |
| Windows | install via winget (`winget search llama.cpp`) or from [llama.cpp releases](https://github.com/ggml-org/llama.cpp/releases) |
| Linux | Download from [llama.cpp releases](https://github.com/ggml-org/llama.cpp/releases), or build from source |

The first run fetches the GGUF from Hugging Face via `llama-server -hf <owner/repo>`. Subsequent runs use the local cache (`~/.cache/llama.cpp` or `HF_HOME`). Ensure outbound internet access on first use.

---

## Usage

### One-liner (coming in a future release)

> **Not yet available.** The method below depends on `DmonHostBuilder.AddProvider(IProviderExtension)` being exposed by dmon-core. That hook is planned for a future release. Once it lands, registration will collapse to:

```csharp
// FUTURE — not yet implemented
await DmonHost.CreateBuilder(args)
    .UseLlamaCpp("unsloth/gemma-4-26B-A4B-it-qat-GGUF")
    .Build()
    .RunAsync();
```

### Interim usage (works today)

Until the dmon-core hosting API is available you can register the extension manually. This path is illustrative and not yet covered by automated integration tests; the exact API surface is subject to change when the hosting hook lands.

```csharp
var options = new LlamaCppOptions
{
    ModelId = "unsloth/gemma-4-26B-A4B-it-qat-GGUF",
    Quant   = "Q4_K_M",        // optional — default is Q4_K_M
    GpuLayers = 35,            // optional — omit to use CPU only
};

var ext = new LlamaCppProviderExtension(options);

// Resolve IProviderRegistry from the dmon host's DI container
IProviderRegistry registry = host.Services.GetRequiredService<IProviderRegistry>();
await registry.RegisterExtensionAsync(ext);

// Then select llamacpp as the active provider/model via the dmon session API
```

---

## Options

Configure via `LlamaCppOptions` or set environment variables before starting the host.

| Property | Type | Default | Env var | Description |
|----------|------|---------|---------|-------------|
| `ModelId` | `string` | _(required)_ | `LLAMA_MODEL_ID` | Hugging Face repo path (`owner/repo-GGUF`); required but enforced at startup (`EnsureRunningAsync`), not at construction — `FromEnvironment()` leaves it empty if the env var is unset |
| `Quant` | `string` | `Q4_K_M` | `LLAMA_QUANT` | GGUF quantisation suffix passed to `-hf` |
| `ServerPath` | `string?` | `null` (resolved from PATH) | `LLAMA_SERVER_PATH` | Explicit path to the `llama-server` binary |
| `Port` | `int?` | `null` (free port) | `LLAMA_PORT` | Fixed port; omit to auto-select a free port |
| `ContextSize` | `int?` | `null` (server default) | `LLAMA_CONTEXT_SIZE` | Passed as `-c <n>` |
| `GpuLayers` | `int?` | `null` (CPU only) | `LLAMA_GPU_LAYERS` | Passed as `-ngl <n>`; set to a large number to offload all layers |
| `ExtraArgs` | `IReadOnlyList<string>` | `[]` | — | Additional raw arguments appended to the `llama-server` command line |
| `ReadyTimeout` | `TimeSpan` | `120s` | — | How long to wait for `GET /health` to return ready before throwing `TimeoutException` |
| `Host` | `string` | `127.0.0.1` | `LLAMA_HOST` | Bind address passed to `llama-server --host` |

`LlamaCppOptions.FromEnvironment()` constructs an options record from the env vars in the table above.

---

## Tool-calling behaviour

`llama-server` is always launched with `--jinja` to enable Jinja2 chat-template rendering, which is required for tool-call formatting.

After the server reaches readiness, the extension runs a **startup probe**: it sends a single tool-call request and checks whether the response includes a properly formed tool invocation. The outcome is stored in `LlamaCppRuntimeState.ToolCallingVerified` and surfaced via `ChatClientCapabilities.SupportsToolCalling` — both through the `CapabilitiesDecorator` and on the `ModelInfo` entries returned by `ListModelsAsync`.

- If the probe round-trips successfully: `SupportsToolCalling = true`.
- If the model or its chat template cannot produce tool calls: `SupportsToolCalling = false` and a warning is emitted. Tool calling is **disabled for the session** — the agent falls back to plain text.

The probe never guesses capability from the model name or GGUF filename. The result is authoritative for the session.

---

## Building this repo (contributors)

The `nuget.config` at the repo root points at a local feed:

```
../dmon-core/build/core-feed
```

This feed supplies `Dmon.Abstractions` so the build works offline. To populate it:

1. Clone [dmon-core](https://github.com/daemonicai/dmon-core) as a sibling directory (`../dmon-core` relative to this repo).
2. In `dmon-core`, run `dotnet pack` — this writes packages into `build/core-feed`.
3. Return to this repo and run `dotnet restore` / `dotnet build`.

Alternatively, if `Dmon.Abstractions` is available on another configured feed (e.g. a private GitHub Packages feed), add that source to your local `nuget.config` or `~/.nuget/NuGet/NuGet.Config`.

```sh
dotnet build Dmon.Extensions.LlamaCpp.slnx
dotnet test  Dmon.Extensions.LlamaCpp.slnx
dotnet pack  src/Dmon.Extensions.LlamaCpp/Dmon.Extensions.LlamaCpp.csproj -o ./artifacts
```
