// demo — the dmon showcase composition root.
//
// This is the agent we demo in front of an audience. Update it whenever a
// feature becomes ready to show the world.
//
// The story it tells: the conversation runs entirely on a local MLX model, so
// nothing you say leaves the laptop. Only the grounded web-search query egresses
// to Gemini, and only when the model chooses to call the tool.
//
// ---------------------------------------------------------------------------
// RUN (from the repo root):
//
//   bash demo/build.sh
//   export DMON_CORE_PATH="$PWD/build/demo/Agent.dll"
//   cd demo && dotnet run --project ../frontends/Dmon.Terminal
//
// Re-run build.sh after editing this file or any provider/tool it references.
// ---------------------------------------------------------------------------
//
// Prerequisites:
//   - macOS on Apple Silicon (arm64) with `uv` on PATH. The first turn
//     provisions ~/.dmon/mlx/venv (mlx_lm >= 0.31.3) and loads the model, so
//     WARM IT UP before going on stage — do not let an audience watch a 7GB
//     model load.
//   - GEMINI_API_KEY set in the environment. The web_search tool needs it; the
//     local driving model does not.
//
// WHY #:project AND NOT #:package:
//   The published 0.2.0 packages predate this demo's headline verb — NuGet's
//   Dmon.Providers.Mlx 0.2.0 exports only AddMlxFirstline/AddMlxEscalation, not
//   UseMlx (which landed later, in PR #104). Local project refs are the only way
//   to demo work that is merged but not yet released. Revisit after the next
//   release if this should double as copy-pasteable user documentation.
//
//   Do NOT mix these #:project refs with any first-party #:package — that
//   combination produces NU1605.
//
// WHY THIS FILE IS NOT NAMED Dmon.cs:
//   CoreResolver tier 1 claims any Dmon.cs in the working directory and builds
//   it via CoreProcessManager.BuildFileBasedProgramAsync, which passes
//   -p:ManagePackageVersionsCentrally=false. That strips the versions off
//   #:project refs to this repo's CPM-managed projects and fails with NU1015 in
//   every one of them. Naming this Agent.cs keeps tier 1 from claiming it, so a
//   host launched with cwd=demo/ falls through to tier 2 (DMON_CORE_PATH) and
//   picks up the prebuilt dll instead. sandbox-code/Agent.cs carries the same
//   scar for the same reason.
//
// Publish opt-outs mirror sandbox-code/Agent.cs: a file-based program defaults
// to trimming/AOT, which fails a framework-dependent (--no-self-contained)
// publish with NETSDK1102.
#:property PublishAot=false
#:property PublishTrimmed=false
#:property PublishSingleFile=false
#:property UseAppHost=false
#:project ../core/Dmon.Core/Dmon.Core.csproj
#:project ../providers/Dmon.Providers.Mlx/Dmon.Providers.Mlx.csproj
#:project ../providers/Dmon.Providers.Gemini/Dmon.Providers.Gemini.csproj
#:project ../tools/Dmon.Tools.WebSearch/Dmon.Tools.WebSearch.csproj

using Dmon.Hosting;

// gemma-4-e4b on the OptiQ-4bit quant. The quant matters: nvfp4 over-quantises
// at E4B scale into unusable tool calling (rambling, ad-hoc JSON, glyph
// corruption), and nvfp4 has wider MLX problems besides — it is not a quant to
// reach for. OptiQ-4bit is the verified-clean tool-calling checkpoint.
//
// Port 8666 stays clear of the daemon's firstline (8800) and escalation (8810)
// runtimes, so this can run alongside a daemon without colliding.
await DmonHost.CreateBuilder(args)
    .UseMlx("mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit", port: 8666)
    .AddAgentWebSearch(p => p.UseGemini("gemini-2.5-flash"))
    .Build()
    .RunAsync();
