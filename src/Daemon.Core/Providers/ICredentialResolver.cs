namespace Daemon.Core.Providers;

public interface ICredentialResolver
{
    ValueTask<string?> ResolveAsync(string providerName, CancellationToken cancellationToken = default);
}
