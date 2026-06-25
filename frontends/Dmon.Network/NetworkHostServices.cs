using Dmon.Network.DeviceKeys;
using Dmon.Network.Sessions;
using Dmon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dmon.Network;

/// <summary>
/// Extension method that registers all DI services used by the network host.
/// Extracted from Program.cs so the registration graph can be validated in tests.
/// </summary>
internal static class NetworkHostServices
{
    /// <summary>
    /// Registers every DI service required by the network host (hosted services, singletons,
    /// and factory-lambda registrations). Does NOT register configuration (IOptions bindings)
    /// or configure middleware/routes — those stay in Program.cs.
    /// </summary>
    internal static IServiceCollection AddNetworkHostServices(
        this IServiceCollection services,
        NetworkDeviceKeyPaths deviceKeyPaths,
        DeviceKeySet startupKeySet)
    {
        services.AddSingleton(deviceKeyPaths);
        services.AddSingleton(new DeviceKeySetProvider(startupKeySet));

        // --- Core infrastructure (D6 — reuse Dmon.Runtime bootstrap) ---
        services.AddSingleton<ICoreLauncher, CoreLauncher>();

        // --- Time provider (injectable for testability) ---
        services.AddSingleton(TimeProvider.System);

        // --- Device-key store hot-reload watcher ---
        services.AddHostedService<DeviceKeyStoreWatcher>();

        // --- Last-seen telemetry writer (network-host-owned; sole writer of lastseen.json) ---
        services.AddSingleton<LastSeenWriter>();

        // --- Session registry, reaper, WS endpoint handler, and device-connection index ---
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton<DeviceConnectionIndex>();
        services.AddHostedService<SessionReaper>();
        services.AddSingleton<NetworkConnectionEndpoint>(sp =>
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

        return services;
    }
}
