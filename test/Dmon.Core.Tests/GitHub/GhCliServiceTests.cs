using Dmon.Core.GitHub;

namespace Dmon.Core.Tests.GitHub;

public sealed class GhCliServiceTests
{
    [Fact]
    public async Task IsAvailableAsync_ReturnsTrue_WhenGhInstalledAndAuthenticated()
    {
        GhCliService service = new(_ => Task.FromResult(true));

        bool result = await service.IsAvailableAsync(CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsAvailableAsync_ReturnsFalse_WhenGhNotInstalled()
    {
        // Simulates the OS failing to launch the process: throws Win32Exception, caught internally, returns false.
        // This matches the outer catch path in RunGhAuthStatusAsync (gh binary not on PATH).
        GhCliService service = new(_ =>
        {
            try { throw new System.ComponentModel.Win32Exception("No such file or directory"); }
            catch { return Task.FromResult(false); }
        });

        bool result = await service.IsAvailableAsync(CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_ReturnsFalse_WhenGhNotAuthenticated()
    {
        // Simulates gh returning a non-zero exit code (probe returns false)
        GhCliService service = new(_ => Task.FromResult(false));

        bool result = await service.IsAvailableAsync(CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_CachesResult_ProbeCalledOnlyOnce()
    {
        int callCount = 0;
        GhCliService service = new(_ =>
        {
            callCount++;
            return Task.FromResult(true);
        });

        await service.IsAvailableAsync(CancellationToken.None);
        await service.IsAvailableAsync(CancellationToken.None);
        await service.IsAvailableAsync(CancellationToken.None);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task IsAvailableAsync_CachesFalseResult_ProbeCalledOnlyOnce()
    {
        int callCount = 0;
        GhCliService service = new(_ =>
        {
            callCount++;
            return Task.FromResult(false);
        });

        await service.IsAvailableAsync(CancellationToken.None);
        bool result = await service.IsAvailableAsync(CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.False(result);
    }
}
