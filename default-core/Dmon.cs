// dmon default composition root — the prebuilt default core (no extensions).
// Build: dotnet build default-core/Dmon.cs
// Run:   dotnet run --no-build default-core/Dmon.cs
#:package dmoncore@0.2.*

using Dmon.Hosting;

await DmonHost.CreateBuilder(args).Build().RunAsync();
