// dmon default composition root — the prebuilt default core (no extensions).
// Build: dotnet build default-core/Dmon.cs
// Run:   dotnet run --no-build default-core/Dmon.cs
#:property AssemblyName=dmoncore
#:property PackageId=Dmon.DefaultCore
#:property PublishAot=false
#:property PublishTrimmed=false
#:property PublishSingleFile=false
#:property UseAppHost=false
// SQLitePCLRaw.lib.e_sqlite3 2.1.11 (transitive via Microsoft.Data.Sqlite, pulled in by dmoncore)
// carries GHSA-2m69-gcr7-jv3q (high). Suppressed until Microsoft.Data.Sqlite ships a version
// that pulls in SQLitePCLRaw >= 2.1.12 where the advisory is fixed.
#:property NoWarn=$(NoWarn);NU1903
#:package dmoncore@0.2.*
#:package Dmon.Providers.Anthropic@0.2.*
#:package Dmon.Providers.OpenAI@0.2.*
#:package Dmon.Providers.Gemini@0.2.*
#:package Dmon.Providers.Ollama@0.2.*
#:package Dmon.Providers.Mtplx@0.2.*
#:package Dmon.Tools.Builtin@0.2.*

using Dmon.Hosting;

await DmonHost.CreateBuilder(args)
    .UseAnthropic()
    .UseOpenAI()
    .UseGemini()
    .UseOllama()
    .UseMtplx()
    .AddBuiltinTools()
    .Build()
    .RunAsync();
