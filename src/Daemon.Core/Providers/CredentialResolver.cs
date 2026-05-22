using Daemon.Core.Auth;

namespace Daemon.Core.Providers;

/// <summary>
/// Resolves credentials per ADR-005: environment variable → credentials file.
/// The interactive prompt step (3rd in the resolution order) is handled by
/// <see cref="IAuthService"/> during the explicit <c>auth.login</c> flow and
/// is not triggered by this resolver during normal client creation.
/// </summary>
public sealed class CredentialResolver : ICredentialResolver
{
    private readonly IReadOnlyDictionary<string, ProviderConfig> _configs;
    private readonly ICredentialFileStore _fileStore;

    public CredentialResolver(IEnumerable<ProviderConfig> configs, ICredentialFileStore fileStore)
    {
        _configs = configs.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        _fileStore = fileStore;
    }

    public async ValueTask<string?> ResolveAsync(string providerName, CancellationToken cancellationToken = default)
    {
        if (!_configs.TryGetValue(providerName, out ProviderConfig? config))
        {
            return null;
        }

        // Step 1: environment variable
        if (config.Auth.EnvVar is not null)
        {
            string? envValue = Environment.GetEnvironmentVariable(config.Auth.EnvVar);

            if (!string.IsNullOrWhiteSpace(envValue))
            {
                return envValue;
            }
        }

        // Step 2: credentials file
        CredentialRecord? record = await _fileStore.ReadAsync(providerName, cancellationToken).ConfigureAwait(false);

        if (record is not null && !string.IsNullOrWhiteSpace(record.ApiKey))
        {
            return record.ApiKey;
        }

        // Step 3: interactive prompt — handled by AuthService during auth.login,
        // not triggered here during normal client creation.
        return null;
    }
}