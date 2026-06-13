using Dmon.Core.Config;
using Dmon.Core.Profiles;
using Dmon.Abstractions.Profiles;
using Dmon.Gateway;
using Dmon.Gateway.DeviceKeys;
using Dmon.Gateway.Sessions;
using Dmon.Runtime;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
builder.Services.Configure<GatewayOptions>(
    builder.Configuration.GetSection(GatewayOptions.SectionName));

// Apply the bind address from config before building, defaulting to loopback (D5).
GatewayOptions gatewayOptions = builder.Configuration
    .GetSection(GatewayOptions.SectionName)
    .Get<GatewayOptions>() ?? new GatewayOptions();

// 9.1 — Enforce the loopback-by-default / no-public-NIC policy before starting the host.
(bool bindAllowed, string? bindError) = GatewayBindPolicy.Validate(
    gatewayOptions.BindAddress, gatewayOptions.AllowNonLoopbackBind);

if (!bindAllowed)
{
    Console.Error.WriteLine($"[dmon-gateway] FATAL: {bindError}");
    return 1;
}

if (GatewayBindPolicy.IsNonLoopbackWithOptIn(
        gatewayOptions.BindAddress, gatewayOptions.AllowNonLoopbackBind))
{
    Console.WriteLine(
        $"[dmon-gateway] WARNING: Binding to non-loopback address '{gatewayOptions.BindAddress}'. " +
        "The intended exposure path is 'tailscale serve' fronting the loopback bind, not a " +
        "direct non-loopback bind. Ensure your firewall rules are correct.");
}

builder.WebHost.UseUrls(gatewayOptions.BindAddress);

// --- Device-key store: resolve paths and load once at startup ---
string deviceKeyStoreDir = string.IsNullOrEmpty(gatewayOptions.DeviceKeyStoreDirectory)
    ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dmon", "gateway")
    : gatewayOptions.DeviceKeyStoreDirectory;

GatewayDeviceKeyPaths deviceKeyPaths = new(
    DevicesPath: Path.Combine(deviceKeyStoreDir, "devices.json"),
    LastSeenPath: Path.Combine(deviceKeyStoreDir, "lastseen.json"));

DeviceKeySet startupKeySet;
if (!File.Exists(deviceKeyPaths.DevicesPath))
{
    // Absent file → first-run state; auth disabled (fail-open on absent, fail-closed on malformed).
    startupKeySet = DeviceKeySet.Empty;
}
else
{
    try
    {
        startupKeySet = await DeviceKeyStoreReader.ReadAsync(deviceKeyPaths.DevicesPath)
            .ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[dmon-gateway] FATAL: Failed to load device-key store '{deviceKeyPaths.DevicesPath}': {ex.Message}");
        return 1;
    }
}

builder.Services.AddSingleton(deviceKeyPaths);
builder.Services.AddSingleton(new DeviceKeySetProvider(startupKeySet));

// --- Core infrastructure (D6 — reuse Dmon.Runtime bootstrap) ---
builder.Services.AddSingleton<ICoreLauncher, CoreLauncher>();

// --- Profile resolution (Dmon.Core; matches DaemonServiceExtensions wiring) ---
builder.Services.AddSingleton<EffectiveProfileSetResolver>();
builder.Services.AddSingleton(new GatewayProfilePaths(
    UserConfigPath: Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dmon", "config.yaml"),
    ProjectConfigPath: Path.Combine(
        Directory.GetCurrentDirectory(),
        ".dmon", "config.yaml")));
builder.Services.AddSingleton<IAgentProfileResolver>(sp =>
{
    EffectiveProfileSetResolver setResolver = sp.GetRequiredService<EffectiveProfileSetResolver>();
    GatewayProfilePaths paths = sp.GetRequiredService<GatewayProfilePaths>();
    return new AgentProfileResolver(setResolver, paths.UserConfigPath, paths.ProjectConfigPath);
});

// --- Time provider (injectable for testability — Group 7) ---
builder.Services.AddSingleton(TimeProvider.System);

// --- Device-key store hot-reload watcher ---
builder.Services.AddHostedService<DeviceKeyStoreWatcher>();

// --- Session registry, reaper, WS endpoint handler, and device-connection index ---
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<DeviceConnectionIndex>();
builder.Services.AddHostedService<SessionReaper>();
builder.Services.AddSingleton<GatewayConnectionEndpoint>();

WebApplication app = builder.Build();

// --- WebSocket middleware (D1 — ASP.NET Core built-in, no SignalR) ---
app.UseWebSockets();

// --- WebSocket endpoint: connection-control sub-protocol (Group 3) ---
app.MapGet("/ws", (HttpContext context, GatewayConnectionEndpoint endpoint) =>
    endpoint.HandleAsync(context));

await app.RunAsync().ConfigureAwait(false);
return 0;
