using Dmon.Core.Config;
using Dmon.Core.Profiles;
using Dmon.Abstractions.Profiles;
using Dmon.Gateway;
using Dmon.Gateway.Sessions;
using Dmon.Runtime;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
builder.Services.Configure<GatewayOptions>(
    builder.Configuration.GetSection(GatewayOptions.SectionName));

// Apply the bind address from config before building, defaulting to loopback (D5).
GatewayOptions gatewayOptions = builder.Configuration
    .GetSection(GatewayOptions.SectionName)
    .Get<GatewayOptions>() ?? new GatewayOptions();

builder.WebHost.UseUrls(gatewayOptions.BindAddress);

// --- Core infrastructure (D6 — reuse Dmon.Runtime bootstrap) ---
builder.Services.AddSingleton<CoreLauncher>();

// --- Profile resolution (Dmon.Core; matches DaemonServiceExtensions wiring) ---
builder.Services.AddSingleton<EffectiveProfileSetResolver>();
builder.Services.AddSingleton<IAgentProfileResolver>(sp =>
{
    EffectiveProfileSetResolver setResolver = sp.GetRequiredService<EffectiveProfileSetResolver>();
    string userConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dmon", "config.yaml");
    string projectConfigPath = Path.Combine(
        Directory.GetCurrentDirectory(),
        ".dmon", "config.yaml");
    return new AgentProfileResolver(setResolver, userConfigPath, projectConfigPath);
});

// --- Session registry (group 2 adds handler lifecycle) ---
builder.Services.AddSingleton<SessionRegistry>();

WebApplication app = builder.Build();

// --- WebSocket middleware (D1 — ASP.NET Core built-in, no SignalR) ---
app.UseWebSockets();

// --- WebSocket endpoint (group 2-3 add session attach/control sub-protocol) ---
app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using System.Net.WebSockets.WebSocket socket =
        await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

    // Placeholder: accept and immediately close. Groups 2-3 wire session attach,
    // the control sub-protocol, and the JSONL relay here.
    await socket.CloseAsync(
        System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
        "gateway not yet implemented",
        context.RequestAborted).ConfigureAwait(false);
});

await app.RunAsync().ConfigureAwait(false);
