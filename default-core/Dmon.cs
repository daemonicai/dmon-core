// dmon default composition root — the prebuilt default core (no extensions).
// Build: dotnet build default-core/Dmon.cs
// Run:   dotnet run --no-build default-core/Dmon.cs
#:property AssemblyName=dmoncore
#:property PackageId=Dmon.DefaultCore
#:property PublishAot=false
#:property PublishTrimmed=false
#:property PublishSingleFile=false
#:property UseAppHost=false
#:package dmoncore@0.2.*
#:package Dmon.Providers.Anthropic@0.2.*
#:package Dmon.Providers.OpenAI@0.2.*
#:package Dmon.Providers.Gemini@0.2.*
#:package Dmon.Providers.Ollama@0.2.*

using Dmon.Hosting;

await DmonHost.CreateBuilder(args)
    .UseAnthropic()
    .UseOpenAI()
    .UseGemini()
    .UseOllama()
    .Build()
    .RunAsync();
