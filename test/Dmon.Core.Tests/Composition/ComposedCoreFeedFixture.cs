using System.Reflection;
using Dmon.Tests.Shared;

namespace Dmon.Core.Tests.Composition;

/// <summary>
/// xUnit class fixture that packs dmoncore, its contract trio, and
/// <c>Dmon.SampleExtension</c> into a unique temporary NuGet feed directory.
/// One pack per fixture instance; the feed is deleted in teardown.
/// Unique directories prevent races when <c>dotnet test</c> runs multiple
/// test assemblies in parallel.
/// </summary>
public sealed class ComposedCoreFeedFixture : IAsyncLifetime
{
    private string? _tempFeed;

    /// <summary>
    /// Absolute path to the temp NuGet feed directory. Available after
    /// <see cref="InitializeAsync"/> completes.
    /// </summary>
    public string FeedPath => _tempFeed
        ?? throw new InvalidOperationException("Fixture not yet initialised.");

    public async Task InitializeAsync()
    {
        _tempFeed = Path.Combine(
            Path.GetTempPath(),
            $"dmon-feed-{Guid.NewGuid():N}");

        string repoRoot = LocateRepoRoot();
        string packScript = Path.Combine(repoRoot, "scripts", "pack-core.sh");

        await RunAsync("bash", $"\"{packScript}\" \"{_tempFeed}\"", repoRoot);
    }

    public Task DisposeAsync()
    {
        if (_tempFeed is not null)
        {
            try { Directory.Delete(_tempFeed, recursive: true); } catch { /* best effort */ }
        }

        return Task.CompletedTask;
    }

    private static string LocateRepoRoot()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        // test/Dmon.Core.Tests/bin/Release/net10.0 → repo root is 5 levels up
        return Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));
    }

    private static async Task RunAsync(string fileName, string arguments, string workingDirectory)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            fileName, arguments, workingDirectory, TimeSpan.FromMinutes(5));

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pack-core.sh failed (exit {result.ExitCode}){result.TruncatedNote}.\n" +
                $"stdout: {result.StandardOutput}\nstderr: {result.StandardError}");
        }
    }
}
