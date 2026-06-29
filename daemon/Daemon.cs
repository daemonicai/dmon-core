// dmon Daemon composition root — the personal-assistant agent (triage + dcal + dmail + memory).
// Build: dotnet build daemon/Daemon.cs -c Release
// Run:   dotnet run --no-build daemon/Daemon.cs
#:project ../core/Dmon.Core/Dmon.Core.csproj
#:project Daemon.Routing/Daemon.Routing.csproj
#:project ../tools/Dmon.Tools.Dcal/Dmon.Tools.Dcal.csproj
#:project ../tools/Dmon.Tools.Dmail/Dmon.Tools.Dmail.csproj
#:project ../memory/Dmon.Memory/Dmon.Memory.csproj
#:project ../providers/Dmon.Providers.Mlx/Dmon.Providers.Mlx.csproj
#:project ../providers/Dmon.Providers.Gemini/Dmon.Providers.Gemini.csproj

using GeminiDotnet;
using GeminiDotnet.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Dmon.Hosting;
using Dmon.Memory;
using Dmon.Providers.Mlx;
using Dmon.Tools.Dmail;

DmonHostBuilder builder = DmonHost.CreateBuilder(args);

string geminiKey   = builder.Configuration.GetValue<string>("GEMINI_API_KEY",    string.Empty)!;
string egressModel = builder.Configuration.GetValue<string>("DMON_EGRESS_MODEL", "gemini-3.1-flash-lite")!;

IChatClient egress = new GeminiChatClient(new GeminiClientOptions
{
    ApiKey  = geminiKey,
    ModelId = egressModel,
});

builder.AddMlxFirstline();
builder.AddMlxEscalation();
builder.UseTriage    (sp => sp.MlxClient(MlxRuntimeKeys.Firstline));
builder.AddEscalation(sp => sp.MlxClient(MlxRuntimeKeys.Escalation));
builder.AddEgress    (egress);
builder.AddEscalationWarming(sp => sp.GetRequiredKeyedService<MlxProviderExtension>(MlxRuntimeKeys.Escalation));
builder.AddDcalAbilities();
builder.AddToolExtension(new DmailExtension());
builder.Services.AddDmonMemory();

await builder.Build().RunAsync();
