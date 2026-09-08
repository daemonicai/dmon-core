// sandbox-code — a local-first MLX coding agent (throwaway dev harness).
//
// A standalone dmon composition root: a single local MLX runtime as the active
// provider plus the builtin coding tools. Wired via local #:project references
// (not NuGet #:package) so it always tracks the working tree.
//
// WHY THIS FILE IS NOT NAMED Dmon.cs:
//   `dmon`'s tier-1 launcher builds any cwd `Dmon.cs` with
//   `-p:ManagePackageVersionsCentrally=false`, which strips the versions off
//   #:project refs to this repo's CPM-managed projects (NU1015). So we can't be
//   launched via tier-1. Instead we publish to a dll and launch via tier-2
//   (DMON_CORE_PATH). Naming this Agent.cs keeps tier-1 from grabbing it, so
//   `cd sandbox-code && dmon` falls through to the prebuilt core below.
//
// BUILD (run ./build.sh, or):
//   dotnet publish sandbox-code/Agent.cs -c Release \
//       -o build/sandbox-code --no-self-contained
//
// RUN (from repo root):
//   export DMON_CORE_PATH="$PWD/build/sandbox-code/Agent.dll"
//   cd sandbox-code && dmon
//
// Prerequisites: macOS on Apple Silicon (arm64) with `uv` on PATH. First turn
// provisions ~/.dmon/mlx/venv (mlx_lm >= 0.31.3) and loads the model.
//
// Publish opt-outs mirror default-core/Dmon.cs: a file-based program defaults to
// trimming/AOT, which fails a framework-dependent (--no-self-contained) publish.
#:property PublishAot=false
#:property PublishTrimmed=false
#:property PublishSingleFile=false
#:property UseAppHost=false
#:project ../core/Dmon.Core/Dmon.Core.csproj
#:project ../providers/Dmon.Providers.Mlx/Dmon.Providers.Mlx.csproj
#:project ../tools/Dmon.Tools.Builtin/Dmon.Tools.Builtin.csproj

using Dmon.Hosting;

// gemma-4-26B-A4B: 26B total / ~4B active (MoE) — strong coder, near-4B speed.
// nvfp4 tool-calling is verified at 26B scale. Port 8666 stays clear of the
// daemon's firstline (8800) / escalation (8810) runtimes.
await DmonHost.CreateBuilder(args)
    .UseMlx("mlx-community/gemma-4-26B-A4B-it-qat-nvfp4", port: 8666)
    .AddBuiltinTools()
    .Build()
    .RunAsync();
