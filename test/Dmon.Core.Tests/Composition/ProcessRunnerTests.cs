using System.Diagnostics;
using Dmon.Tests.Shared;

namespace Dmon.Core.Tests.Composition;

/// <summary>
/// Proves the shared <see cref="ProcessRunner"/> never blocks past its drain grace when a
/// descendant process still holds the output pipes open — the shape that hung
/// <c>InitFeedFixture</c>/<c>ComposedCoreFeedFixture</c> when a leftover MSBuild worker
/// node inherited a redirected pipe from a <c>dotnet pack</c> child.
/// </summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_DescendantHoldsPipeOpen_ReturnsPromptlyWithTruncatedPartialOutput()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ProcessResult result = await ProcessRunner.RunAsync(
            "bash", "-c \"sleep 30 & echo $!\"", Path.GetTempPath(),
            timeout: TimeSpan.FromSeconds(10), drainGrace: TimeSpan.FromSeconds(1));
        stopwatch.Stop();

        string pidLine = result.StandardOutput.Trim();
        try
        {
            // The bound only discriminates against the bug (waiting for EOF, which the
            // backgrounded sleep would delay by ~30s) if it is comfortably under that.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"RunAsync took {stopwatch.Elapsed} — expected it to return promptly, not wait for the backgrounded sleep.");
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.OutputTruncated);
            Assert.False(string.IsNullOrWhiteSpace(pidLine));
        }
        finally
        {
            if (int.TryParse(pidLine, out int pid)) TryKill(pid);
        }
    }

    [Fact]
    public async Task RunAsync_NormalProcess_ReturnsFullUntruncatedOutput()
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "bash", "-c \"echo hi\"", Path.GetTempPath(),
            timeout: TimeSpan.FromSeconds(10), drainGrace: TimeSpan.FromSeconds(1));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.OutputTruncated);
        Assert.Equal("hi", result.StandardOutput.Trim());
    }

    private static void TryKill(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch { /* best effort — process may already be gone */ }
    }
}
