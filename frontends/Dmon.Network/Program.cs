using Dmon.Network;
using Dmon.Network.DeviceKeys;
using Dmon.Network.Sessions;
using Dmon.Runtime;
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

bool effectiveNonLoopback = NetworkBindPolicy.IsNonLoopbackWithOptIn(
    networkOptions.BindAddress, networkOptions.AllowNonLoopbackBind);

if (effectiveNonLoopback)
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

// ADR-036: an empty/absent device-key set is auth-disabled only on a loopback bind; on a
// non-loopback bind it fails closed. Announce the effective posture at startup so the operator
// knows whether they must pair a device before any /ws upgrade will succeed.
if (startupKeySet.IsEmpty)
{
    Console.WriteLine(effectiveNonLoopback
        ? "[dmon-network] Authentication is FAIL-CLOSED: the device-key store is empty/absent on a " +
          "non-loopback bind; every /ws upgrade is rejected (401) until a device is paired."
        : "[dmon-network] Authentication is disabled: the device-key store is empty/absent on a " +
          "loopback bind; every /ws upgrade is authorized (local-dev convenience).");
}

builder.Services.AddNetworkHostServices(deviceKeyPaths, startupKeySet);

WebApplication app = builder.Build();

// --- WebSocket middleware (D1 — ASP.NET Core built-in, no SignalR) ---
app.UseWebSockets();

// --- WebSocket endpoint: connection-control sub-protocol (Group 3) ---
app.MapGet("/ws", (HttpContext context, NetworkConnectionEndpoint endpoint) =>
    endpoint.HandleAsync(context));

await app.RunAsync().ConfigureAwait(false);
return 0;
