# Dmon.Providers.Mtplx

Managed Apple-Silicon [MTPLX](https://github.com/daemonicai/mtplx) provider for the [dmon](https://github.com/daemonicai/dmon-core) coding agent.

This package attaches to a locally running `mtplx serve` process — an OpenAI-compatible server for Apple-Silicon multi-token-prediction (MTP) inference — and exposes it as a `Microsoft.Extensions.AI` `IChatClient`. It does not spawn the server; it expects `mtplx serve` to already be running (attach-first lifecycle), probes tool-calling capability at session start, and wires the result into `ChatClientCapabilities.SupportsToolCalling`.

---

## Prerequisites

`mtplx` must be installed and `mtplx serve` must be running before the dmon agent starts. This package does **not** launch the server.

---

## Options

Configure via `MtplxOptions` or set environment variables before starting the host.

| Property | Type | Default | Env var | Description |
|----------|------|---------|---------|-------------|
| `Host` | `string` | `127.0.0.1` | `MTPLX_HOST` | Host address of the running `mtplx serve` process |
| `Port` | `int` | `8000` | `MTPLX_PORT` | Port the server is listening on |
| `ModelId` | `string?` | `null` | `MTPLX_MODEL_ID` | Model identifier to request; when unset, targets the server's currently active model |
| `ServerPath` | `string?` | `null` | `MTPLX_SERVER_PATH` | Reserved for future use — path to the `mtplx` binary if dmon is to launch it |
| `ReadyTimeout` | `TimeSpan` | `120s` | — | How long to wait for the server health check before throwing `TimeoutException` |

`MtplxOptions.FromEnvironment()` constructs an options record from the env vars in the table above.

---

## Building from the monorepo (contributors)

`Dmon.Providers.Mtplx` lives under `providers/` in the [dmon-core monorepo](https://github.com/daemonicai/dmon-core). `Dmon.Abstractions` is consumed via `ProjectReference` — no local feed or manual pack step is required.

```sh
# build / test the whole solution (recommended)
dotnet build Everything.slnx -c Release
dotnet test  Everything.slnx -c Release

# pack the provider package
dotnet pack providers/Dmon.Providers.Mtplx/Dmon.Providers.Mtplx.csproj -c Release -o ./artifacts
```
