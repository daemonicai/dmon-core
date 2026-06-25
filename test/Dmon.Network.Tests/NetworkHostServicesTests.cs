using Dmon.Network.DeviceKeys;
using Dmon.Network.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dmon.Network;

/// <summary>
/// Validates that <see cref="NetworkHostServices.AddNetworkHostServices"/> produces a DI graph
/// that can construct every hosted service and singleton the network host relies on.
///
/// This test exists to catch the class of bug where a DI-resolved type has an internal (not public)
/// constructor — which Microsoft.Extensions.DependencyInjection cannot call — or where a required
/// service registration is missing entirely. Without explicit resolution of hosted services, a
/// <c>ValidateOnBuild = true</c> build does NOT construct hosted services or factory-lambda
/// registrations and would pass even with the bug present.
/// </summary>
public sealed class NetworkHostServicesTests
{
    [Fact]
    public void AddNetworkHostServices_ResolvesCriticalServices_WithoutThrowing()
    {
        // Arrange — minimal registrations that mirror what Program.cs provides before calling
        // AddNetworkHostServices: logging + IOptions binding for NetworkOptions.
        string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        NetworkDeviceKeyPaths paths = new(
            DevicesPath: Path.Combine(tempDir, "devices.json"),
            LastSeenPath: Path.Combine(tempDir, "lastseen.json"));

        ServiceCollection services = new();
        services.AddLogging();
        services.Configure<NetworkOptions>(_ => { });
        services.AddNetworkHostServices(paths, DeviceKeySet.Empty);

        // Act — build with full validation. ValidateOnBuild catches missing registrations for
        // non-factory, non-hosted-service singletons but does NOT construct hosted services or
        // factory-lambda singletons; the explicit resolves below are mandatory for the full check.
        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        // Assert — explicitly resolve the types that have internal ctors (the original crash) and
        // the factory-lambda singleton. Do NOT call StartAsync; we only verify constructability.

        // Resolves IHostedService registrations — constructs DeviceKeyStoreWatcher AND SessionReaper.
        // Before the fix (ctor internal), this line throws:
        //   "A suitable constructor for type 'DeviceKeyStoreWatcher' could not be located."
        IHostedService[] hostedServices = provider.GetServices<IHostedService>().ToArray();
        Assert.NotEmpty(hostedServices);
        Assert.Contains(hostedServices, s => s is DeviceKeyStoreWatcher);
        Assert.Contains(hostedServices, s => s is SessionReaper);

        // Resolves the DI ctor of LastSeenWriter (NetworkDeviceKeyPaths overload).
        // Before the fix (ctor internal), DI throws on resolution.
        LastSeenWriter lastSeenWriter = provider.GetRequiredService<LastSeenWriter>();
        Assert.NotNull(lastSeenWriter);

        // Resolves the factory-lambda NetworkConnectionEndpoint.
        NetworkConnectionEndpoint endpoint = provider.GetRequiredService<NetworkConnectionEndpoint>();
        Assert.NotNull(endpoint);
    }
}
