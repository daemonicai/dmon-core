// dmon Daemon composition root — the personal-assistant agent (triage + dcal + dmail + memory).
// Build: dotnet build daemon/Daemon.cs -c Release
// Run:   dotnet run --no-build daemon/Daemon.cs
#:project ../core/Dmon.Core/Dmon.Core.csproj
#:project Daemon.Routing/Daemon.Routing.csproj
#:project ../tools/Dmon.Tools.Dcal/Dmon.Tools.Dcal.csproj
#:project ../tools/Dmon.Tools.Dmail/Dmon.Tools.Dmail.csproj
#:project ../memory/Dmon.Memory/Dmon.Memory.csproj
#:project ../providers/Dmon.Providers.Ollama/Dmon.Providers.Ollama.csproj
#:project ../providers/Dmon.Providers.OpenAI/Dmon.Providers.OpenAI.csproj
#:project ../providers/Dmon.Providers.Gemini/Dmon.Providers.Gemini.csproj

using GeminiDotnet;
using GeminiDotnet.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;
using Dmon.Hosting;
using Dmon.Memory;
using Dmon.Tools.Dmail;

string e2bUrl    = Environment.GetEnvironmentVariable("DCAL_E2B_URL")      ?? "http://localhost:11434";
string reasonerUrl = Environment.GetEnvironmentVariable("DCAL_REASONER_URL") ?? "http://localhost:8080/v1";
string geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")    ?? string.Empty;

// e2b backend: Ollama (used as both classifier and e2b-with-tools inside TriageRouterFactory).
IChatClient e2b = new OllamaApiClient(new Uri(e2bUrl), "gemma4:e2b-it-qat");

// Reasoner backend: OpenAI-compatible local endpoint, no credential required.
IChatClient reasoner = new OpenAI.Chat.ChatClient(
        "gemma4-27b",
        new System.ClientModel.ApiKeyCredential("not-needed"),
        new OpenAI.OpenAIClientOptions { Endpoint = new Uri(reasonerUrl) })
    .AsIChatClient();

// Egress backend: Gemini via native GeminiDotnet client.
IChatClient egress = new GeminiChatClient(new GeminiClientOptions
{
    ApiKey  = geminiKey,
    ModelId = "gemini-2.5-flash",
});

DmonHostBuilder builder = DmonHost.CreateBuilder(args);
builder.UseTriage(e2b);
builder.AddReasoner(reasoner);
builder.AddEgress(egress);
builder.AddDcalAbilities();
builder.AddToolExtension(new DmailExtension());
builder.Services.AddDmonMemory();

await builder.Build().RunAsync();
