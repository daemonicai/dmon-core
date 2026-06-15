using Dmon.Gateway;
using Dmon.Gateway.DeviceKeys;
using Dmon.Gateway.Sessions;
using Dmon.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

// --- Time provider (injectable for testability) ---
builder.Services.AddSingleton(TimeProvider.System);

// --- Device-key store hot-reload watcher ---
builder.Services.AddHostedService<DeviceKeyStoreWatcher>();

// --- Last-seen telemetry writer (gateway-owned; sole writer of lastseen.json) ---
builder.Services.AddSingleton<LastSeenWriter>();

// --- Session registry, reaper, WS endpoint handler, and device-connection index ---
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<DeviceConnectionIndex>();
builder.Services.AddHostedService<SessionReaper>();
builder.Services.AddSingleton<GatewayConnectionEndpoint>(sp =>
{
    GatewayOptions opts = sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue;
    string? workspaceRoot = string.IsNullOrEmpty(opts.WorkspaceRoot)
        ? null
        : opts.WorkspaceRoot;
    return new GatewayConnectionEndpoint(
        sp.GetRequiredService<SessionRegistry>(),
        sp.GetRequiredService<DeviceConnectionIndex>(),
        sp.GetRequiredService<ICoreLauncher>(),
        workspaceRoot,
        sp.GetRequiredService<DeviceKeySetProvider>(),
        sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<LastSeenWriter>(),
        sp.GetRequiredService<ILogger<GatewayConnectionEndpoint>>());
});

WebApplication app = builder.Build();

// --- WebSocket middleware (D1 — ASP.NET Core built-in, no SignalR) ---
app.UseWebSockets();

// --- WebSocket endpoint: connection-control sub-protocol (Group 3) ---
app.MapGet("/ws", (HttpContext context, GatewayConnectionEndpoint endpoint) =>
    endpoint.HandleAsync(context));

await app.RunAsync().ConfigureAwait(false);
return 0;
