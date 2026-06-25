using Dmon.Network;
using Dmon.Network.DeviceKeys;
using Dmon.Network.Sessions;
using Dmon.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
builder.Services.Configure<NetworkOptions>(
    builder.Configuration.GetSection(NetworkOptions.SectionName));

// Apply the bind address from config before building, defaulting to loopback (D5).
NetworkOptions networkOptions = builder.Configuration
    .GetSection(NetworkOptions.SectionName)
    .Get<NetworkOptions>() ?? new NetworkOptions();

// 9.1 — Enforce the loopback-by-default / no-public-NIC policy before starting the host.
(bool bindAllowed, string? bindError) = NetworkBindPolicy.Validate(
    networkOptions.BindAddress, networkOptions.AllowNonLoopbackBind);

if (!bindAllowed)
{
    Console.Error.WriteLine($"[dmon-network] FATAL: {bindError}");
    return 1;
}

if (NetworkBindPolicy.IsNonLoopbackWithOptIn(
        networkOptions.BindAddress, networkOptions.AllowNonLoopbackBind))
{
    Console.WriteLine(
        $"[dmon-network] WARNING: Binding to non-loopback address '{networkOptions.BindAddress}'. " +
        "The intended exposure path is 'tailscale serve' fronting the loopback bind, not a " +
        "direct non-loopback bind. Ensure your firewall rules are correct.");
}

builder.WebHost.UseUrls(networkOptions.BindAddress);

// --- Device-key store: resolve paths and load once at startup ---
string deviceKeyStoreDir = string.IsNullOrEmpty(networkOptions.DeviceKeyStoreDirectory)
    ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dmon", "network")
    : networkOptions.DeviceKeyStoreDirectory;

NetworkDeviceKeyPaths deviceKeyPaths = new(
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
            $"[dmon-network] FATAL: Failed to load device-key store '{deviceKeyPaths.DevicesPath}': {ex.Message}");
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

// --- Last-seen telemetry writer (network-host-owned; sole writer of lastseen.json) ---
builder.Services.AddSingleton<LastSeenWriter>();

// --- Session registry, reaper, WS endpoint handler, and device-connection index ---
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<DeviceConnectionIndex>();
builder.Services.AddHostedService<SessionReaper>();
builder.Services.AddSingleton<NetworkConnectionEndpoint>(sp =>
{
    NetworkOptions opts = sp.GetRequiredService<IOptionsMonitor<NetworkOptions>>().CurrentValue;
    string? workspaceRoot = string.IsNullOrEmpty(opts.WorkspaceRoot)
        ? null
        : opts.WorkspaceRoot;
    return new NetworkConnectionEndpoint(
        sp.GetRequiredService<SessionRegistry>(),
        sp.GetRequiredService<DeviceConnectionIndex>(),
        sp.GetRequiredService<ICoreLauncher>(),
        workspaceRoot,
        sp.GetRequiredService<DeviceKeySetProvider>(),
        sp.GetRequiredService<IOptionsMonitor<NetworkOptions>>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<LastSeenWriter>(),
        sp.GetRequiredService<ILogger<NetworkConnectionEndpoint>>());
});

WebApplication app = builder.Build();

// --- WebSocket middleware (D1 — ASP.NET Core built-in, no SignalR) ---
app.UseWebSockets();

// --- WebSocket endpoint: connection-control sub-protocol (Group 3) ---
app.MapGet("/ws", (HttpContext context, NetworkConnectionEndpoint endpoint) =>
    endpoint.HandleAsync(context));

await app.RunAsync().ConfigureAwait(false);
return 0;
