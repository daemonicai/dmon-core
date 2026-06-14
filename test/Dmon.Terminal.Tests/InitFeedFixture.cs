using System.Diagnostics;
using System.Reflection;

namespace Dmon.Terminal.Tests;

/// <summary>
/// xUnit class fixture that packs dmoncore and its contract trio (Dmon.Protocol,
/// Dmon.Abstractions, Dmon.Extensions) into a unique temporary NuGet feed directory.
/// One pack per fixture instance; the feed is deleted in teardown.
/// Unique directories prevent races when <c>dotnet test</c> runs multiple
/// test assemblies in parallel.
/// </summary>
public sealed class InitFeedFixture : IAsyncLifetime
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
            $"dmon-init-feed-{Guid.NewGuid():N}");

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
        // test/Dmon.Terminal.Tests/bin/Release/net10.0 → repo root is 5 levels up
        return Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));
    }

    private static async Task RunAsync(string fileName, string arguments, string workingDirectory)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process proc = new() { StartInfo = psi };
        proc.Start();

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"pack-core.sh timed out after 5 minutes.");
        }

        string output = await stdoutTask;
        string errors = await stderrTask;

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pack-core.sh failed (exit {proc.ExitCode}).\nstdout: {output}\nstderr: {errors}");
        }
    }
}
