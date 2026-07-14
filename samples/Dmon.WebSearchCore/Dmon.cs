// dmon web-search composition root — offline driving model + hosted web-search brain.
// The agent runs locally on Ollama (no cloud egress for conversation) while the
// web_search tool delegates to Gemini 2.5 Flash for grounded search queries only.
// Build: dotnet build samples/Dmon.WebSearchCore/Dmon.cs
// Run:   dotnet run --no-build samples/Dmon.WebSearchCore/Dmon.cs
// SQLitePCLRaw.lib.e_sqlite3 2.1.11 (transitive via Microsoft.Data.Sqlite, pulled in by dmoncore)
// carries GHSA-2m69-gcr7-jv3q (high). Suppressed until Microsoft.Data.Sqlite ships a version
// that pulls in SQLitePCLRaw >= 2.1.12 where the advisory is fixed.
#:property NoWarn=$(NoWarn);NU1903
#:package dmoncore@0.2.*
#:package Dmon.Providers.Ollama@0.2.*
#:package Dmon.Providers.Gemini@0.2.*
#:package Dmon.Tools.WebSearch@0.2.*

using Dmon.Hosting;

await DmonHost.CreateBuilder(args)
    .UseOllama("http://localhost:11434", "llama3.2")
    .AddAgentWebSearch(p => p.UseGemini("gemini-2.5-flash"))
    .Build()
    .RunAsync();
