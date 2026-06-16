using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;

namespace Dmon.Hosting;

/// <summary>
/// <see cref="IChatClientFactory"/> produced by <see cref="SubAgentProviderRegistration.Build"/>.
/// Defers credential resolution and client construction to the first <see cref="CreateAsync"/> call.
/// The constructed client is memoized — "single-turn" (ADR-010 D4) is a property of how the
/// tool uses the client (a fresh message list per call), not of client lifetime.
/// </summary>
public sealed class SubAgentChatClientFactory : IChatClientFactory, IDisposable
{
    private readonly IProviderFactory _factory;
    private readonly string _activeModel;

    // Memoized client — non-null after the first successful CreateAsync.
    private IChatClient? _cached;
    private readonly SemaphoreSlim _lock = new(1, 1);

    internal SubAgentChatClientFactory(IProviderFactory factory, string activeModel)
    {
        _factory = factory;
        _activeModel = activeModel;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cached?.Dispose();
        _lock.Dispose();
    }

    /// <summary>
    /// Returns (and memoizes) the <see cref="IChatClient"/> for this sub-agent.
    /// Resolves the provider credential from <c>DefaultEnvVar</c> lazily on first call.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the required environment variable (<c>DefaultEnvVar</c>) is not set.
    /// </exception>
    public async ValueTask<IChatClient> CreateAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            string envVar = _factory.DefaultEnvVar;
            string? apiKey = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    $"The sub-agent provider requires the environment variable '{envVar}' to be set, but it was not found or was empty.");
            }

            // Build a minimal ProviderConfig from the validated registration.
            ModelRef? modelRef = ModelRef.Parse(_activeModel);
            string modelId = modelRef?.Model ?? _factory.DefaultModelId;

            ProviderConfig config = new()
            {
                Name = _factory.AdapterName,
                Adapter = _factory.AdapterName,
                DefaultModelId = modelId,
                Auth = new ProviderAuthConfig
                {
                    Type = "apiKey",
                    EnvVar = envVar,
                },
            };

            _cached = await _factory.CreateAsync(config, apiKey, cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }
}
