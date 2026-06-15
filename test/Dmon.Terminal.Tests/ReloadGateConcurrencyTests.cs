namespace Dmon.Terminal.Tests;

/// <summary>
/// Regression tests for the concurrent-reload race fixed in Group 3 of
/// host-rpc-abstraction: <c>requestReload</c> (running on the DrainAsync task) and
/// the point-B dispose-and-swap (running on the main loop task) must never interleave.
///
/// Without the <c>reloadGate</c> lock, a <c>requestReload</c> call that lands between
/// <c>sessionCts.Dispose()</c> and the assignment of the new CTS calls <c>Cancel()</c>
/// on an already-disposed instance, throwing <see cref="ObjectDisposedException"/> and
/// causing a spurious app shutdown.
///
/// The tests below simulate that exact interleaving using semaphores to force the
/// two tasks into the race window, then assert that no exception escapes and that the
/// loop neither shuts down spuriously nor misses a legitimate subsequent reload.
/// </summary>
public sealed class ReloadGateConcurrencyTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Replicates the Program.cs reload-gate pattern (lines 57-68 and 165-169) under test.
    /// Returns (restartCount, exceptionEscaped).
    ///
    /// ANTI-DRIFT: this helper must stay in sync with Program.cs.
    /// - <c>requestReload</c> lambda mirrors Program.cs line 67:
    ///   <c>() => { lock (reloadGate) { if (!reloadClosed) sessionCts.Cancel(); } }</c>
    /// - Point-B swap mirrors Program.cs lines 137-141:
    ///   <c>lock (reloadGate) { sessionCts.Dispose(); sessionCts = ...; }</c>
    /// - Shutdown finally mirrors Program.cs lines 165-169:
    ///   <c>lock (reloadGate) { reloadClosed = true; sessionCts.Dispose(); }</c>
    /// </summary>
    private static async Task<(int restartCount, Exception? escapedException)>
        SimulateReloadLoopAsync(
            bool useLock,
            SemaphoreSlim swapHoldGate,
            SemaphoreSlim reloadFireGate,
            CancellationToken outerToken)
    {
        using CancellationTokenSource outerCts =
            CancellationTokenSource.CreateLinkedTokenSource(outerToken);

        object reloadGate = new();
        bool reloadClosed = false;
        CancellationTokenSource sessionCts =
            CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);

        int restartCount = 0;
        Exception? escapedException = null;

        // Simulates requestReload() as called from DrainAsync (Program.cs line 67).
        void RequestReload()
        {
            if (useLock)
            {
                lock (reloadGate) { if (!reloadClosed) sessionCts.Cancel(); }
            }
            else
            {
                sessionCts.Cancel();   // no protection — exhibits the bug
            }
        }

        // Simulates a single pass through the point-B dispose-and-swap,
        // pausing mid-swap to let the concurrent RequestReload() race in.
        Task SwapAsync() => Task.Run(async () =>
        {
            // Signal that we are entering the swap window.
            swapHoldGate.Release();

            // Wait for the test to say "go" — both threads start at the same time.
            await reloadFireGate.WaitAsync(outerToken).ConfigureAwait(false);

            restartCount++;

            if (useLock)
            {
                lock (reloadGate)
                {
                    sessionCts.Dispose();
                    sessionCts = CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);
                }
            }
            else
            {
                // Yield between dispose and swap to maximise the race window.
                sessionCts.Dispose();
                await Task.Yield();
                sessionCts = CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);
            }
        }, outerToken);

        // Simulates the concurrent DrainAsync reload invocation.
        Task ReloadTask() => Task.Run(() =>
        {
            try
            {
                RequestReload();
            }
            catch (Exception ex)
            {
                escapedException = ex;
                // In the real app this catch-all in DrainAsync cancels the outer cts.
                outerCts.Cancel();
            }
        }, outerToken);

        // Wait until SwapAsync is poised at the race window entrance.
        Task swap = SwapAsync();
        await swapHoldGate.WaitAsync(outerToken).ConfigureAwait(false);

        // Release both tasks simultaneously to force the interleaving.
        Task reload = ReloadTask();
        reloadFireGate.Release();

        await Task.WhenAll(swap, reload).ConfigureAwait(false);

        return (restartCount, escapedException);
    }

    /// <summary>
    /// Replicates the shutdown-dispose-vs-reload race (Program.cs finally block, lines 165-169).
    /// Forces a <c>requestReload</c> to race the shutdown <c>finally</c> dispose and asserts
    /// no <see cref="ObjectDisposedException"/> escapes when the gate/flag fix is in place.
    ///
    /// ANTI-DRIFT: mirrors Program.cs:
    /// - Shutdown finally: <c>lock (reloadGate) { reloadClosed = true; sessionCts.Dispose(); }</c>
    /// - requestReload:    <c>lock (reloadGate) { if (!reloadClosed) sessionCts.Cancel(); }</c>
    /// </summary>
    private static async Task<Exception?> SimulateShutdownDisposeRaceAsync(
        bool useFlagGuard,
        SemaphoreSlim disposeReadyGate,
        SemaphoreSlim reloadFireGate,
        CancellationToken outerToken)
    {
        using CancellationTokenSource outerCts =
            CancellationTokenSource.CreateLinkedTokenSource(outerToken);

        object reloadGate = new();
        bool reloadClosed = false;
        CancellationTokenSource sessionCts =
            CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);

        Exception? escapedException = null;

        // Simulates the shutdown finally block (Program.cs lines 165-169).
        Task ShutdownDisposeAsync() => Task.Run(async () =>
        {
            // Signal that we are about to dispose.
            disposeReadyGate.Release();

            // Wait until the test fires both sides simultaneously.
            await reloadFireGate.WaitAsync(outerToken).ConfigureAwait(false);

            if (useFlagGuard)
            {
                lock (reloadGate) { reloadClosed = true; sessionCts.Dispose(); }
            }
            else
            {
                // No flag — dispose runs unguarded; reload can race in.
                sessionCts.Dispose();
            }
        }, outerToken);

        // Simulates requestReload() on the drainTask (still live during shutdown).
        Task ReloadTask() => Task.Run(() =>
        {
            try
            {
                if (useFlagGuard)
                {
                    lock (reloadGate) { if (!reloadClosed) sessionCts.Cancel(); }
                }
                else
                {
                    sessionCts.Cancel();   // no protection — exhibits the bug
                }
            }
            catch (Exception ex)
            {
                escapedException = ex;
            }
        }, outerToken);

        Task shutdown = ShutdownDisposeAsync();
        await disposeReadyGate.WaitAsync(outerToken).ConfigureAwait(false);

        Task reload = ReloadTask();
        reloadFireGate.Release();

        await Task.WhenAll(shutdown, reload).ConfigureAwait(false);

        return escapedException;
    }

    // ── regression: bug is detectable ─────────────────────────────────────────

    /// <summary>
    /// Demonstrates that the point-B race IS detectable: without the lock, running the
    /// concurrent-reload scenario many times eventually produces an
    /// <see cref="ObjectDisposedException"/> (or at minimum never hides the hazard
    /// under the current runtime).
    ///
    /// This test is expected to encounter the exception reliably because the
    /// <see cref="Task.Yield"/> in <c>SwapAsync</c> maximises the window.
    /// If the runtime happens to serialise the two tasks on every single run the
    /// test may pass — but in practice the race manifests within a few iterations.
    ///
    /// NOTE: this is a negative test verifying the test harness is sensitive enough
    /// to detect the bug. It passes when the bug IS present (i.e., exception observed).
    /// </summary>
    [Fact]
    public async Task WithoutLock_ConcurrentReload_ExhibitsObjectDisposedException()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        bool exceptionSeen = false;
        for (int i = 0; i < 200 && !exceptionSeen; i++)
        {
            using SemaphoreSlim swapHold = new(0, 1);
            using SemaphoreSlim reloadFire = new(0, 1);

            (_, Exception? ex) = await SimulateReloadLoopAsync(
                useLock: false,
                swapHoldGate: swapHold,
                reloadFireGate: reloadFire,
                outerToken: timeout.Token);

            if (ex is ObjectDisposedException)
                exceptionSeen = true;
        }

        Assert.True(exceptionSeen,
            "Expected ObjectDisposedException to appear during concurrent reload without the lock.");
    }

    // ── fix: locked gate prevents the exception ────────────────────────────────

    /// <summary>
    /// With the <c>reloadGate</c> lock in place, running two concurrent reload
    /// requests across the restart window never produces an
    /// <see cref="ObjectDisposedException"/> and does not cancel the outer
    /// <see cref="CancellationTokenSource"/>.
    /// </summary>
    [Fact]
    public async Task WithLock_ConcurrentReload_NoExceptionAndNoSpuriousShutdown()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        for (int i = 0; i < 50; i++)
        {
            using SemaphoreSlim swapHold = new(0, 1);
            using SemaphoreSlim reloadFire = new(0, 1);

            (int restartCount, Exception? ex) = await SimulateReloadLoopAsync(
                useLock: true,
                swapHoldGate: swapHold,
                reloadFireGate: reloadFire,
                outerToken: timeout.Token);

            Assert.Null(ex);
            Assert.Equal(1, restartCount);
        }
    }

    // ── regression: shutdown-dispose race is also detectable ──────────────────

    /// <summary>
    /// Demonstrates that the shutdown-finally-dispose race is detectable: without the
    /// <c>reloadClosed</c> flag (and gate), a <c>requestReload</c> call that races the
    /// shutdown <c>finally</c> dispose produces an <see cref="ObjectDisposedException"/>.
    ///
    /// This is a NEGATIVE canary — it passes when the bug IS present.
    /// </summary>
    [Fact]
    public async Task WithoutFlagGuard_ShutdownDisposeRace_ExhibitsObjectDisposedException()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        bool exceptionSeen = false;
        for (int i = 0; i < 200 && !exceptionSeen; i++)
        {
            using SemaphoreSlim disposeReady = new(0, 1);
            using SemaphoreSlim reloadFire = new(0, 1);

            Exception? ex = await SimulateShutdownDisposeRaceAsync(
                useFlagGuard: false,
                disposeReadyGate: disposeReady,
                reloadFireGate: reloadFire,
                outerToken: timeout.Token);

            if (ex is ObjectDisposedException)
                exceptionSeen = true;
        }

        Assert.True(exceptionSeen,
            "Expected ObjectDisposedException to appear when shutdown dispose races requestReload without the flag guard.");
    }

    // ── fix: flag guard prevents the shutdown-dispose race ────────────────────

    /// <summary>
    /// With the <c>reloadClosed</c> flag (set under <c>reloadGate</c> before dispose)
    /// in place, a <c>requestReload</c> racing the shutdown <c>finally</c> dispose
    /// never produces an <see cref="ObjectDisposedException"/> — it is silently skipped.
    ///
    /// Mirrors the Program.cs fix at lines 165-169 (finally block) and line 67 (requestReload).
    /// </summary>
    [Fact]
    public async Task WithFlagGuard_ShutdownDisposeRace_NoException()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        for (int i = 0; i < 50; i++)
        {
            using SemaphoreSlim disposeReady = new(0, 1);
            using SemaphoreSlim reloadFire = new(0, 1);

            Exception? ex = await SimulateShutdownDisposeRaceAsync(
                useFlagGuard: true,
                disposeReadyGate: disposeReady,
                reloadFireGate: reloadFire,
                outerToken: timeout.Token);

            Assert.Null(ex);
        }
    }

    // ── invariant: a /reload after the swap drives a legitimate second restart ─

    /// <summary>
    /// A reload request that arrives *after* the point-B swap is completed must
    /// be honoured — the gate must not over-suppress legitimate subsequent reloads.
    /// </summary>
    [Fact]
    public async Task WithLock_ReloadAfterSwap_CancelsNewCts()
    {
        using CancellationTokenSource outerCts = new();
        object reloadGate = new();
        bool reloadClosed = false;
        CancellationTokenSource sessionCts =
            CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);

        // Simulate point B (swap completes fully).
        lock (reloadGate)
        {
            sessionCts.Dispose();
            sessionCts = CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);
        }

        // A subsequent requestReload() should cancel the fresh CTS.
        CancellationToken freshToken = sessionCts.Token;
        lock (reloadGate) { if (!reloadClosed) sessionCts.Cancel(); }

        Assert.True(freshToken.IsCancellationRequested,
            "requestReload() after point-B swap must cancel the newly created CTS.");

        await Task.CompletedTask; // keep async signature consistent
    }
}
