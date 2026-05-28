using Dcli;
using Dcli.Testing;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Tier-B integration test: drives <see cref="TerminalRenderer"/> against a real
/// <see cref="HeadlessTerminal"/> to confirm the renderer produces visible output in the
/// dcli live window and status row.
/// </summary>
public sealed class TerminalRendererHarnessTests
{
    [Fact]
    public async Task StreamingSequence_ProducesLiveContentAndStatusRow()
    {
        await using HeadlessTerminal harness = await HeadlessTerminal.StartAsync(
            new HeadlessTerminalOptions { InitialColumns = 80, InitialRows = 24 });

        TerminalRenderer renderer = new(harness.Terminal);

        // 1. Set status before streaming begins.
        renderer.SetStatus("test-model", thinking: false);
        await harness.SettleAsync();

        // 2. Stream tokens — both should land in the live window.
        renderer.AppendToken("Hello");
        renderer.AppendToken(", world");
        await harness.SettleAsync();

        FrameSnapshot mid = harness.Snapshot;

        // The live window must contain the streamed text.
        string liveText = string.Concat(
            mid.LiveWindowRows.SelectMany(l => l.Segments).Select(s => s.Text));
        Assert.Contains("Hello", liveText);
        Assert.Contains(", world", liveText);

        // The status row must reflect the model name.
        string statusText = string.Concat(
            mid.FixedRegionRows.SelectMany(l => l.Segments).Select(s => s.Text));
        Assert.Contains("test-model", statusText);

        // 3. Commit on SettleTurn — live window clears (content crosses the commit horizon).
        renderer.SettleTurn("Hello, world");
        await harness.SettleAsync();

        FrameSnapshot after = harness.Snapshot;
        // After commit the live window should be empty (content moved to scrollback).
        Assert.Empty(after.LiveWindowRows);
    }
}
