namespace Daemon.Core.Providers;

public sealed class EnvironmentCredentialResolver : ICredentialResolver
{
    private readonly IReadOnlyDictionary<string, ProviderConfig> _configs;

    public EnvironmentCredentialResolver(IEnumerable<ProviderConfig> configs)
    {
        _configs = configs.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public ValueTask<string?> ResolveAsync(string providerName, CancellationToken cancellationToken = default)
    {
        if (!_configs.TryGetValue(providerName, out ProviderConfig? config))
        {
            return ValueTask.FromResult<string?>(null);
        }

        if (config.Auth.EnvVar is null)
        {
            return ValueTask.FromResult<string?>(null);
        }

        string? value = Environment.GetEnvironmentVariable(config.Auth.EnvVar);
        return ValueTask.FromResult(value);
    }
}
