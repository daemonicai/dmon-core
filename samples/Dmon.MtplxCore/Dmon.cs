// dmon MTPLX composition root — local Apple-Silicon agent using the MTPLX runtime.
// Attaches to a running MTPLX server or starts one on port 8000. No cloud egress.
// Build: dotnet build samples/Dmon.MtplxCore/Dmon.cs
// Run:   dotnet run --no-build samples/Dmon.MtplxCore/Dmon.cs
// SQLitePCLRaw.lib.e_sqlite3 2.1.11 (transitive via Microsoft.Data.Sqlite, pulled in by dmoncore)
// carries GHSA-2m69-gcr7-jv3q (high). Suppressed until Microsoft.Data.Sqlite ships a version
// that pulls in SQLitePCLRaw >= 2.1.12 where the advisory is fixed.
#:property NoWarn=$(NoWarn);NU1903
#:package dmoncore@0.2.*
#:package Dmon.Providers.Mtplx@0.2.*
#:package Dmon.Tools.Builtin@0.2.*

using Dmon.Hosting;

await DmonHost.CreateBuilder(args)
    .UseMtplx("Youssofal/Qwen3.5-9B")
    .AddBuiltinTools()
    .Build()
    .RunAsync();
