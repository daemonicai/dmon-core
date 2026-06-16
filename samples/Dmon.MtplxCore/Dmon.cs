// dmon MTPLX composition root — local Apple-Silicon agent using the MTPLX runtime.
// Attaches to a running MTPLX server or starts one on port 8000. No cloud egress.
// Build: dotnet build samples/Dmon.MtplxCore/Dmon.cs
// Run:   dotnet run --no-build samples/Dmon.MtplxCore/Dmon.cs
#:package dmoncore@0.2.*
#:package Dmon.Providers.Mtplx@0.2.*
#:package Dmon.Tools.Builtin@0.2.*

using Dmon.Hosting;

await DmonHost.CreateBuilder(args)
    .UseMtplx("Youssofal/Qwen3.5-9B")
    .AddBuiltinTools()
    .Build()
    .RunAsync();
