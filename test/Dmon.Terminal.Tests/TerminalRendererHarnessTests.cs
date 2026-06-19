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

        // 1. Set status before streaming begins (core version required to render the status band).
        renderer.SetReadiness("1.0.0");
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

        // The status fixed-region must contain the model name and readiness text.
        string statusText = string.Concat(
            mid.FixedRegionRows.SelectMany(l => l.Segments).Select(s => s.Text));
        Assert.Contains("test-model", statusText);
        Assert.Contains("[Ready]", statusText);

        // 3. Commit on SettleTurn — live window clears (content crosses the commit horizon).
        renderer.SettleTurn("Hello, world");
        await harness.SettleAsync();

        FrameSnapshot after = harness.Snapshot;
        // After commit the live window should be empty (content moved to scrollback).
        Assert.Empty(after.LiveWindowRows);
    }

    [Fact]
    public async Task SymmetricFrame_PreambleAndPromptAndStatusBand_RendersCorrectly()
    {
        await using HeadlessTerminal harness = await HeadlessTerminal.StartAsync(
            new HeadlessTerminalOptions { InitialColumns = 80, InitialRows = 24 });

        TerminalRenderer renderer = new(harness.Terminal);

        // Set up the full symmetric frame: preamble rule, prompt prefix, readiness status.
        renderer.SetPreamble();
        renderer.SetPromptPrefix();
        renderer.SetReadiness("0.3.0");
        renderer.SetStatus("claude-opus", thinking: false);
        await harness.SettleAsync();

        FrameSnapshot snapshot = harness.Snapshot;

        // The fixed region (status band) must contain the two-row band.
        string fixedText = string.Concat(
            snapshot.FixedRegionRows.SelectMany(l => l.Segments).Select(s => s.Text));
        // Rule row contains ─ characters.
        Assert.Contains("─", fixedText);
        // Readiness row contains version and model.
        Assert.Contains("v0.3.0", fixedText);
        Assert.Contains("claude-opus", fixedText);
        Assert.Contains("[Ready]", fixedText);
        // No protocol string in the pinned frame.
        Assert.DoesNotContain("protocol", fixedText);
    }
}
