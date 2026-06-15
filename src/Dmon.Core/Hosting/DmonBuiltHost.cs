namespace Dmon.Hosting;

/// <summary>
/// A configured and built host returned by <see cref="DmonHostBuilder.Build()"/>.
/// Call <see cref="RunAsync(CancellationToken)"/> to start the JSONL/stdio core loop.
/// </summary>
public sealed class DmonBuiltHost
{
    private readonly IHost _host;

    internal DmonBuiltHost(IHost host)
    {
        _host = host;
    }

    /// <summary>
    /// The service provider for the built host. Allows callers and tests to resolve
    /// registered services without starting the host's background loop.
    /// </summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// Starts the host and runs the JSONL/stdio core loop until the
    /// <paramref name="cancellationToken"/> is cancelled or stdin reaches EOF.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _host.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
