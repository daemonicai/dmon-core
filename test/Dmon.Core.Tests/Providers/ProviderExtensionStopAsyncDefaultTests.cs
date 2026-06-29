using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Providers;

/// <summary>
/// 5.2 — Default no-op StopAsync: a provider that does not override StopAsync gets the DIM,
/// which completes without throwing and touches no external state (ADR-034 D1).
/// </summary>
public sealed class ProviderExtensionStopAsyncDefaultTests
{
    [Fact]
    public async Task StopAsync_Default_CompletesWithoutThrowing()
    {
        // The DIM is only reachable via the interface reference.
        IProviderExtension sut = new StopAsyncDefaultFake();

        Exception? ex = await Record.ExceptionAsync(() => sut.StopAsync());

        Assert.Null(ex);
    }

    [Fact]
    public async Task StopAsync_Default_DoesNotTouchExternalState()
    {
        StopAsyncDefaultFake fake = new();
        // Cast to the interface so the DIM is dispatched (not the concrete type).
        IProviderExtension sut = fake;

        await sut.StopAsync();

        // The fake would flip ExternalStateModified only if real work ran.
        Assert.False(fake.ExternalStateModified,
            "Default StopAsync must not modify any external state.");
    }
}

/// <summary>
/// Minimal IProviderExtension fake that does NOT override StopAsync,
/// exercising the default interface method.
/// </summary>
file sealed class StopAsyncDefaultFake : IProviderExtension
{
    /// <summary>Set by any operation that simulates modifying external state.</summary>
    public bool ExternalStateModified { get; private set; }

    public string ProviderName => "fake-stop-default";
    public bool IsApplicable() => true;

    public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        ExternalStateModified = true;
        return Task.FromResult(true);
    }

    public Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        ExternalStateModified = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        ExternalStateModified = true;
        return Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }

    public IProviderFactory CreateFactory() =>
        throw new NotSupportedException("Not needed in default-StopAsync tests.");

    // StopAsync intentionally NOT overridden — the DIM on IProviderExtension is under test.
}
