using Dmon.Core.Providers;

namespace Dmon.Core.Auth;

public sealed class AuthService : IAuthService
{
    private readonly ICredentialFileStore _fileStore;
    private readonly IReadOnlyDictionary<string, ProviderConfig> _configs;

    public AuthService(IEnumerable<ProviderConfig> configs, ICredentialFileStore fileStore)
    {
        _configs = configs.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        _fileStore = fileStore;
    }

    public async ValueTask<IReadOnlyList<ProviderAuthStatus>> GetStatusAsync(string? providerName = null)
    {
        IEnumerable<KeyValuePair<string, ProviderConfig>> filtered = _configs;

        if (providerName is not null)
        {
            filtered = filtered.Where(kv =>
                string.Equals(kv.Key, providerName, StringComparison.OrdinalIgnoreCase));
        }

        List<ProviderAuthStatus> results = [];

        foreach ((string name, ProviderConfig config) in filtered)
        {
            bool hasEnvVar = config.Auth.EnvVar is not null
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.Auth.EnvVar));

            CredentialRecord? record = await _fileStore.ReadAsync(name).ConfigureAwait(false);
            bool hasFileCredential = record is not null && !string.IsNullOrWhiteSpace(record.ApiKey);

            results.Add(new ProviderAuthStatus
            {
                Provider = name,
                AuthType = config.Auth.Type,
                HasEnvVar = hasEnvVar,
                HasFileCredential = hasFileCredential
            });
        }

        return results;
    }

    public async ValueTask<bool> IsAuthenticatedAsync(string providerName, CancellationToken cancellationToken = default)
    {
        if (!_configs.TryGetValue(providerName, out ProviderConfig? config))
        {
            return false;
        }

        // Check env var
        if (config.Auth.EnvVar is not null
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.Auth.EnvVar)))
        {
            return true;
        }

        // Check credentials file
        CredentialRecord? record = await _fileStore.ReadAsync(providerName, cancellationToken).ConfigureAwait(false);

        return record is not null && !string.IsNullOrWhiteSpace(record.ApiKey);
    }

    public async ValueTask StoreCredentialAsync(
        string providerName,
        string apiKey,
        string headerStyle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string resolvedHeaderStyle = headerStyle;

        if (string.IsNullOrWhiteSpace(resolvedHeaderStyle))
        {
            // Infer header style from adapter if not specified.
            resolvedHeaderStyle = _configs.TryGetValue(providerName, out ProviderConfig? config)
                && string.Equals(config.Adapter, "anthropic", StringComparison.OrdinalIgnoreCase)
                    ? "x-api-key"
                    : "bearer";
        }

        CredentialRecord record = new()
        {
            Provider = providerName,
            CredentialType = "apiKey",
            ApiKey = apiKey,
            HeaderStyle = resolvedHeaderStyle,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _fileStore.WriteAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask LogoutAsync(string providerName, CancellationToken cancellationToken = default)
    {
        await _fileStore.DeleteAsync(providerName, cancellationToken).ConfigureAwait(false);
    }

}