using Dcli;
using Dmon.Terminal.Tests.Fakes;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Tier-A unit tests for <see cref="TerminalRenderer"/> against <see cref="FakeTerminal"/>.
/// </summary>
public sealed class TerminalRendererTests
{
    // ── 1. Streaming sequence ────────────────────────────────────────────────

    [Fact]
    public void AppendToken_FirstToken_OpensLiveBlock()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.AppendToken("Hello");

        Assert.Equal(2, fake.Calls.Count);
        LiveBegun begun = Assert.IsType<LiveBegun>(fake.Calls[0]);
        LiveAppendText appended = Assert.IsType<LiveAppendText>(fake.Calls[1]);
        Assert.Equal(begun.BlockId, appended.BlockId);
        Assert.Equal("Hello", appended.Text);
    }

    [Fact]
    public void AppendToken_SecondToken_AppendsToSameBlock()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.AppendToken("Hello");
        renderer.AppendToken(", world");

        // One LiveBegun, then two LiveAppendText — all with the same BlockId.
        Assert.Equal(3, fake.Calls.Count);
        LiveBegun begun = Assert.IsType<LiveBegun>(fake.Calls[0]);
        LiveAppendText first = Assert.IsType<LiveAppendText>(fake.Calls[1]);
        LiveAppendText second = Assert.IsType<LiveAppendText>(fake.Calls[2]);
        Assert.Equal(begun.BlockId, first.BlockId);
        Assert.Equal(begun.BlockId, second.BlockId);
        Assert.Equal("Hello", first.Text);
        Assert.Equal(", world", second.Text);
    }

    // ── 2. Commit on settle ──────────────────────────────────────────────────

    [Fact]
    public void SettleTurn_CommitsOpenBlock()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.AppendToken("text");
        renderer.SettleTurn(string.Empty);

        LiveBegun begun = Assert.IsType<LiveBegun>(fake.Calls[0]);
        LiveCommitted committed = Assert.IsType<LiveCommitted>(fake.Calls[2]);
        Assert.Equal(begun.BlockId, committed.BlockId);
    }

    [Fact]
    public void AppendToken_AfterSettle_OpensNewBlock()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.AppendToken("first");
        renderer.SettleTurn(string.Empty);

        // Now stream a second turn.
        renderer.AppendToken("second");

        // The second AppendToken must use a new BlockId.
        LiveBegun firstBegun = Assert.IsType<LiveBegun>(fake.Calls[0]);
        LiveBegun secondBegun = Assert.IsType<LiveBegun>(fake.Calls[3]);
        Assert.NotEqual(firstBegun.BlockId, secondBegun.BlockId);
        LiveAppendText secondText = Assert.IsType<LiveAppendText>(fake.Calls[4]);
        Assert.Equal(secondBegun.BlockId, secondText.BlockId);
        Assert.Equal("second", secondText.Text);
    }

    // ── 3. Separator (no label) ──────────────────────────────────────────────

    [Fact]
    public void PrintSeparator_NoLabel_AppendsRuleLineFullWidth()
    {
        FakeTerminal fake = new() { Size = (40, 24) };
        TerminalRenderer renderer = new(fake);

        renderer.PrintSeparator();

        ScrollbackAppendLine call = Assert.IsType<ScrollbackAppendLine>(Assert.Single(fake.Calls));
        string text = string.Concat(call.Line.Segments.Select(s => s.Text));
        Assert.Equal(new string('─', 40), text);
    }

    [Fact]
    public void PrintSeparator_NoLabel_UsesGreyStyling()
    {
        FakeTerminal fake = new() { Size = (20, 10) };
        TerminalRenderer renderer = new(fake);

        renderer.PrintSeparator();

        ScrollbackAppendLine call = Assert.IsType<ScrollbackAppendLine>(Assert.Single(fake.Calls));
        // All segments must have BrightBlack foreground.
        Assert.All(call.Line.Segments, seg =>
            Assert.Equal(Color.Named(Color.AnsiColor.BrightBlack), seg.Style.Foreground));
    }

    // ── 4. Labelled separator ────────────────────────────────────────────────

    [Fact]
    public void PrintSeparator_WithLabel_EmbeddsLabelInRule()
    {
        FakeTerminal fake = new() { Size = (80, 24) };
        TerminalRenderer renderer = new(fake);

        renderer.PrintSeparator("model · thinking…");

        ScrollbackAppendLine call = Assert.IsType<ScrollbackAppendLine>(Assert.Single(fake.Calls));
        string fullText = string.Concat(call.Line.Segments.Select(s => s.Text));

        Assert.Contains("model · thinking…", fullText);
        Assert.Contains("── ", fullText);
        Assert.Contains("─", fullText);
        // Total display width must not exceed terminal columns.
        Assert.True(fullText.Length <= 80);
    }

    // ── 5. Status update ─────────────────────────────────────────────────────

    [Fact]
    public void SetStatus_Thinking_RecordsStatusWithModelAndIndicator()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.SetStatus("claude-opus", thinking: true);

        StatusSet status = Assert.IsType<StatusSet>(Assert.Single(fake.Calls));
        string text = string.Concat(status.Rows.SelectMany(l => l.Segments).Select(s => s.Text));
        Assert.Contains("claude-opus", text);
        Assert.Contains("thinking", text);
    }

    [Fact]
    public void SetStatus_NotThinking_RecordsStatusWithModelNameOnly()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.SetStatus("claude-opus", thinking: false);

        StatusSet status = Assert.IsType<StatusSet>(Assert.Single(fake.Calls));
        string text = string.Concat(status.Rows.SelectMany(l => l.Segments).Select(s => s.Text));
        Assert.Contains("claude-opus", text);
        Assert.DoesNotContain("thinking", text);
    }

    [Fact]
    public void SetStatus_EmptyModelName_ClearsStatus()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.SetStatus(string.Empty, thinking: false);

        StatusSet status = Assert.IsType<StatusSet>(Assert.Single(fake.Calls));
        Assert.Empty(status.Rows);
    }

    // ── 6. User echo ─────────────────────────────────────────────────────────

    [Fact]
    public void AddUserLine_AppendsLineWithBoldPrefix()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.AddUserLine("hi");

        ScrollbackAppendLine call = Assert.IsType<ScrollbackAppendLine>(Assert.Single(fake.Calls));
        // First segment must be bold with the ❯ glyph.
        Segment prefix = call.Line.Segments[0];
        Assert.Contains("❯", prefix.Text);
        Assert.Equal(Format.Bold, prefix.Style.Format);
        // Second segment must contain the message.
        Segment body = call.Line.Segments[1];
        Assert.Equal("hi", body.Text);
    }

    // ── 7. System echo ───────────────────────────────────────────────────────

    [Fact]
    public void AddSystemLine_AppendsLineWithGreyStyling()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.AddSystemLine("a system notice");

        ScrollbackAppendLine call = Assert.IsType<ScrollbackAppendLine>(Assert.Single(fake.Calls));
        Segment seg = Assert.Single(call.Line.Segments);
        Assert.Equal("a system notice", seg.Text);
        Assert.Equal(Color.Named(Color.AnsiColor.BrightBlack), seg.Style.Foreground);
    }

    // ── Extra: SettleTurn with no prior tokens is a no-op ────────────────────

    [Fact]
    public void SettleTurn_WithoutPriorTokens_IsNoOp()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.SettleTurn(string.Empty);

        Assert.Empty(fake.Calls);
    }

    // ── Extra: AppendToken with empty string is a no-op ─────────────────────

    [Fact]
    public void AppendToken_EmptyString_IsNoOp()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.AppendToken(string.Empty);

        Assert.Empty(fake.Calls);
    }
}
