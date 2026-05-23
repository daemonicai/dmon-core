namespace Dmon.Core.Session;

public sealed class SessionLock : IDisposable
{
    private readonly FileStream _lockFile;

    private SessionLock(FileStream lockFile)
    {
        _lockFile = lockFile;
    }

    public static Task<SessionLock> AcquireAsync(string sessionDir, CancellationToken cancellationToken)
    {
        string lockPath = Path.Combine(sessionDir, ".lock");
        try
        {
            FileStream fs = new(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            return Task.FromResult(new SessionLock(fs));
        }
        catch (IOException ex)
        {
            throw new SessionLockedException(sessionDir, ex);
        }
    }

    public void Dispose()
    {
        _lockFile.Dispose();
    }
}

public sealed class SessionLockedException : Exception
{
    public string SessionDirectory { get; }

    public SessionLockedException(string sessionDirectory, Exception inner)
        : base($"Session directory '{sessionDirectory}' is locked by another process.", inner)
    {
        SessionDirectory = sessionDirectory;
    }
}
