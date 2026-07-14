using Dmon.Runtime;

namespace Dmon.Runtime.Tests;

/// <summary>
/// Verifies that <see cref="CoreProcessManager.StopAsync"/> awaits the forced-kill exit so
/// the OS releases the session-directory <c>.lock</c> before the method returns — closing the
/// <c>SessionLockedException</c> respawn race. Driven by a stub core that ignores stdin, so the
/// graceful 500 ms wait times out and the kill path (the one under test) actually runs.
/// </summary>
public sealed class CoreProcessManagerStopTests
{
    [Fact]
    public async Task StopAsync_AwaitsForcedKillExit_ReleasingSessionLock()
    {
        string stubDllPath = Path.Combine(
            Path.GetDirectoryName(typeof(CoreProcessManagerStopTests).Assembly.Location)!,
            "Dmon.StubCore.dll");
        Assert.True(File.Exists(stubDllPath), $"Stub core not found at {stubDllPath}.");

        string workDir = Path.Combine(Path.GetTempPath(), $"dmon-stopkill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        ResolvedCore stub = new(stubDllPath, LaunchMode.DotnetExec);
        using CoreProcessManager mgr = new(stub, workDir);

        try
        {
            await mgr.StartAsync();

            // Read stdout until agentReady — the stub emits it only after taking <workDir>/.lock,
            // so this guarantees the lock is held before we stop.
            await WaitForAgentReadyAsync(mgr.StandardOutput);

            // Graceful 500 ms wait times out (stub ignores stdin) → Kill → bounded await exit.
            await mgr.StopAsync();

            // Exit was awaited: deterministic post-fix, flaky/false pre-fix.
            Assert.False(mgr.IsRunning);

            // The OS released the lock before StopAsync returned — a replacement can re-acquire
            // it without a SessionLockedException. Exclusive re-open is the spec-faithful proxy.
            using FileStream reacquire = new(
                Path.Combine(workDir, ".lock"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task WaitForAgentReadyAsync(TextReader stdout)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        while (true)
        {
            string? line;
            try { line = await stdout.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { break; }

            if (line is null) break;
            if (line.Contains("\"agentReady\"")) return;
        }

        throw new TimeoutException("agentReady was not received within 30 seconds.");
    }
}
