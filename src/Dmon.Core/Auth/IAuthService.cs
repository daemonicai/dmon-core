namespace Dmon.Core.Auth;

/// <summary>
/// Handles auth.login, auth.logout, and auth.status RPC commands per ADR-005.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Returns the authentication state for all configured providers,
    /// or a single provider if <paramref name="providerName"/> is specified.
    /// </summary>
    ValueTask<IReadOnlyList<ProviderAuthStatus>> GetStatusAsync(string? providerName = null);

    /// <summary>
    /// Checks whether credentials are already available for the given provider
    /// (via env var or credentials file).
    /// </summary>
    ValueTask<bool> IsAuthenticatedAsync(string providerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a credential via <see cref="ICredentialFileStore"/> after an
    /// interactive login flow. Used by the auth.login command handler.
    /// </summary>
    ValueTask StoreCredentialAsync(string providerName, string apiKey, string headerStyle, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the credential file for the given provider. Used by the
    /// auth.logout command handler.
    /// </summary>
    ValueTask LogoutAsync(string providerName, CancellationToken cancellationToken = default);
}

public sealed record ProviderAuthStatus
{
    public required string Provider { get; init; }
    public required string AuthType { get; init; }
    public bool HasEnvVar { get; init; }
    public bool HasFileCredential { get; init; }
}