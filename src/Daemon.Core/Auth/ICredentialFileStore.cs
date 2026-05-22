namespace Daemon.Core.Auth;

public interface ICredentialFileStore
{
    /// <summary>
    /// Reads a credential from the user-global store.
    /// Returns null if the file does not exist.
    /// </summary>
    ValueTask<CredentialRecord?> ReadAsync(string providerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes (or overwrites) a credential to the user-global store.
    /// Creates the credentials directory with mode 0700 (POSIX) or restricted ACL (Windows),
    /// and the file with mode 0600 (POSIX) or restricted ACL (Windows).
    /// </summary>
    ValueTask WriteAsync(CredentialRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the credential file for the given provider.
    /// Does not throw if the file does not exist.
    /// </summary>
    ValueTask DeleteAsync(string providerName, CancellationToken cancellationToken = default);
}
