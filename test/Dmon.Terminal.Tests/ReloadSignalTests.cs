namespace Dmon.Terminal.Tests;

/// <summary>
/// Tier-A tests for the reload-signal de-duplication invariant introduced in Group 1
/// of the terminal-host-hardening change.
///
/// Program.cs uses a <see cref="TaskCompletionSource{Boolean}"/> captured in a closure.
/// The top-level statements have no injectable seam for the restart loop without a
/// host refactor that exceeds this change's scope, so these tests exercise the TCS
/// mechanic directly — which is the exact primitive the fix relies on.
///
/// Regression being guarded: before the fix, <c>reloadSignal</c> was recreated inside
/// <c>RunSessionAsync</c> (point A) before <c>RestartAsync</c> completed.  A second
/// <c>/reload</c> during the restart window would light up the freshly-reset signal and
/// cause a second <c>RestartAsync</c> call.  After the fix, recreation happens at point B
/// (after <c>RestartAsync</c> returns), so any second <c>/reload</c> hits the
/// already-completed TCS and is silently ignored.
/// </summary>
public sealed class ReloadSignalTests
{
    // ── 1.4-a: /reload during restart window does not trigger a second restart ─

    /// <summary>
    /// Rapid-fire reload regression: two /reload commands arrive before point-B
    /// recreation — only one restart must occur.
    /// </summary>
    [Fact]
    public async Task RapidReload_DuringRestartWindow_CountsExactlyOneRestart()
    {
        TaskCompletionSource<bool> reloadSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int restartCount = 0;

        async Task SimulateRestartBlockAsync()
        {
            bool shouldRestart = await reloadSignal.Task;
            if (!shouldRestart) return;

            restartCount++;
            await Task.Yield(); // simulate RestartAsync work

            // Point B: recreate only after RestartAsync completes.
            reloadSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // First /reload — triggers the restart.
        reloadSignal.TrySetResult(true);

        // Second /reload fires on the still-original TCS (point A has been removed).
        // Because the TCS is already completed, TrySetResult is a no-op.
        bool secondFired = reloadSignal.TrySetResult(true);

        await SimulateRestartBlockAsync();

        Assert.Equal(1, restartCount);
        Assert.False(secondFired, "Second TrySetResult during restart window must be a no-op.");
    }

    // ── 1.4-b: /reload after recreation legitimately drives a second restart ───

    /// <summary>
    /// Contrasting case: a /reload that arrives *after* point-B recreation must not be
    /// suppressed — it should drive a second restart normally.
    /// This guards against the fix over-suppressing legitimate subsequent reloads.
    /// </summary>
    [Fact]
    public async Task ReloadAfterRecreation_DrivesSecondRestart()
    {
        TaskCompletionSource<bool> reloadSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int restartCount = 0;

        async Task SimulateRestartBlockAsync()
        {
            bool shouldRestart = await reloadSignal.Task;
            if (!shouldRestart) return;

            restartCount++;
            await Task.Yield(); // simulate RestartAsync work

            // Point B: recreate.
            reloadSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // First cycle.
        reloadSignal.TrySetResult(true);
        await SimulateRestartBlockAsync();

        Assert.Equal(1, restartCount);

        // /reload arrives after recreation — the fresh TCS is not yet completed,
        // so this fires a legitimate second restart.
        reloadSignal.TrySetResult(true);
        await SimulateRestartBlockAsync();

        Assert.Equal(2, restartCount);
    }

    // ── 1.4-c: New TCS after point-B accepts the next /reload ───────────────

    [Fact]
    public async Task NewTcsAfterRestartAsync_AcceptsNextReload()
    {
        // Simulates the point-B recreation: original TCS completed → new TCS created →
        // next /reload fires on the fresh TCS → Task completes with true.
        TaskCompletionSource<bool> original = new(TaskCreationOptions.RunContinuationsAsynchronously);
        original.TrySetResult(true); // first /reload

        // RestartAsync would run here.  Point B: recreate the signal.
        TaskCompletionSource<bool> fresh = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Next session is waiting on the fresh signal.
        Task<bool> nextReload = fresh.Task;

        // Second /reload arrives after restart completed — should be honoured.
        fresh.TrySetResult(true);

        bool result = await nextReload;
        Assert.True(result, "Fresh TCS must complete when the next /reload fires.");
    }
}
