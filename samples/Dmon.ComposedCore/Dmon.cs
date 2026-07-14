// dmon composed core — demonstrates compile-time extension composition.
// The extension is a compile-time #:package dependency; no runtime load step.
// Build: dotnet build samples/Dmon.ComposedCore/Dmon.cs
// Run:   dotnet run --no-build samples/Dmon.ComposedCore/Dmon.cs
// SQLitePCLRaw.lib.e_sqlite3 2.1.11 (transitive via Microsoft.Data.Sqlite, pulled in by dmoncore)
// carries GHSA-2m69-gcr7-jv3q (high). Suppressed until Microsoft.Data.Sqlite ships a version
// that pulls in SQLitePCLRaw >= 2.1.12 where the advisory is fixed.
#:property NoWarn=$(NoWarn);NU1903
#:package dmoncore@0.2.*
#:package Dmon.SampleExtension@0.2.*

using Dmon.Hosting;
using Dmon.SampleExtension;

await DmonHost.CreateBuilder(args)
    .AddToolExtension<GreetingExtension>()
    .Build()
    .RunAsync();
