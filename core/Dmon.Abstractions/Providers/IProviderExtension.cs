namespace Dmon.Abstractions.Providers;

/// <summary>
/// Implemented by NuGet extension assemblies that supply a local inference provider
/// (e.g. an Ollama or MLX wrapper). The extension loader detects this interface
/// alongside <c>IDmonExtension</c> and routes it to <c>IProviderRegistry</c>.
/// </summary>
public interface IProviderExtension
{
    /// <summary>Human-readable provider name, e.g. "Ollama" or "MLX".</summary>
    string ProviderName { get; }

    /// <summary>
    /// Returns true if this provider can run on the current platform and hardware.
    /// Called once at extension load time. If false, a Warning is logged and the
    /// provider is not registered. The extension pipeline approval is still stored.
    /// </summary>
    bool IsApplicable();

    /// <summary>
    /// Returns true if the inference server is currently reachable.
    /// Implementations should verify server identity, not just port reachability.
    /// </summary>
    Task<bool> IsRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the server if not running. Only called after an ADR-006
    /// confirmation prompt initiated by the daemon.
    /// </summary>
    Task EnsureRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns models currently available from the running server.
    /// </summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an <see cref="IProviderFactory"/> configured for this runner.
    /// Called after <see cref="IsApplicable"/> returns true; the factory is
    /// registered with <c>ProviderRegistry</c>.
    /// </summary>
    IProviderFactory CreateFactory();

    /// <summary>
    /// Stops a server this provider spawned and owns, releasing its port. The default
    /// is a no-op so attach-only / start-only providers are unaffected (ADR-034 D1).
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
