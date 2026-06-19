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

    [Fact]
    public void PrintSeparator_GoodbyeLabel_EmbedsLabelInScrollback()
    {
        // Verifies the renderer output for the goodbye separator — the ordering fix
        // (before StopAsync rather than after) is enforced in Program.cs where no unit-test
        // seam exists without a host refactor that exceeds this change's scope.
        FakeTerminal fake = new() { Size = (80, 24) };
        TerminalRenderer renderer = new(fake);

        renderer.PrintSeparator("goodbye");

        ScrollbackAppendLine call = Assert.IsType<ScrollbackAppendLine>(Assert.Single(fake.Calls));
        string fullText = string.Concat(call.Line.Segments.Select(s => s.Text));
        Assert.Contains("goodbye", fullText);
        // Separator rule characters must be present on both sides.
        Assert.Contains("─", fullText);
        Assert.All(call.Line.Segments, seg =>
            Assert.Equal(Color.Named(Color.AnsiColor.BrightBlack), seg.Style.Foreground));
    }

    // ── 5. Status update ─────────────────────────────────────────────────────

    [Fact]
    public void SetStatus_BeforeVersionSet_ClearsStatus()
    {
        // No SetReadiness call → _coreVersion is empty → status cleared.
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.SetStatus("claude-opus", thinking: true);

        StatusSet status = Assert.IsType<StatusSet>(Assert.Single(fake.Calls));
        Assert.Empty(status.Rows);
    }

    [Fact]
    public void SetStatus_Thinking_RecordsStatusWithModelAndIndicator()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.SetReadiness("1.0.0");
        fake.ClearCalls(); // discard the SetReadiness call; test SetStatus independently

        renderer.SetStatus("claude-opus", thinking: true);

        StatusSet status = Assert.IsType<StatusSet>(Assert.Single(fake.Calls));
        string text = string.Concat(status.Rows.SelectMany(l => l.Segments).Select(s => s.Text));
        Assert.Contains("claude-opus", text);
        Assert.Contains("Thinking", text);
    }

    [Fact]
    public void SetStatus_NotThinking_RecordsStatusWithIdleIndicator()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.SetReadiness("1.0.0");
        fake.ClearCalls();

        renderer.SetStatus("claude-opus", thinking: false);

        StatusSet status = Assert.IsType<StatusSet>(Assert.Single(fake.Calls));
        string text = string.Concat(status.Rows.SelectMany(l => l.Segments).Select(s => s.Text));
        Assert.Contains("claude-opus", text);
        Assert.Contains("Idle", text);
        Assert.DoesNotContain("Thinking", text);
    }

    [Fact]
    public void SetStatus_EmptyModelName_NoCoreVersion_ClearsStatus()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.SetStatus(string.Empty, thinking: false);

        StatusSet status = Assert.IsType<StatusSet>(Assert.Single(fake.Calls));
        Assert.Empty(status.Rows);
    }

    [Fact]
    public void SetStatus_TwoRows_RuleRowThenReadinessRow()
    {
        FakeTerminal fake = new() { Size = (80, 24) };
        TerminalRenderer renderer = new(fake);

        renderer.SetReadiness("1.2.3");
        fake.ClearCalls();

        renderer.SetStatus("my-model", thinking: false);

        StatusSet status = Assert.IsType<StatusSet>(Assert.Single(fake.Calls));
        Assert.Equal(2, status.Rows.Count);

        // First row: full-width rule.
        string ruleText = string.Concat(status.Rows[0].Segments.Select(s => s.Text));
        Assert.All(ruleText.ToCharArray(), c => Assert.Equal('─', c));
        Assert.Equal(80, ruleText.Length);

        // Second row: readiness with version, model, and state.
        string readinessText = string.Concat(status.Rows[1].Segments.Select(s => s.Text));
        Assert.Contains("[Ready]", readinessText);
        Assert.Contains("v1.2.3", readinessText);
        Assert.Contains("my-model", readinessText);
        Assert.Contains("Idle", readinessText);
        Assert.DoesNotContain("protocol", readinessText);
    }

    [Fact]
    public void SetStatus_ThinkingTrue_ReadinessRowContainsThinkingIndicator()
    {
        FakeTerminal fake = new() { Size = (80, 24) };
        TerminalRenderer renderer = new(fake);

        renderer.SetReadiness("2.0.0");
        fake.ClearCalls();

        renderer.SetStatus("gpt-4", thinking: true);

        StatusSet status = Assert.IsType<StatusSet>(Assert.Single(fake.Calls));
        string readinessText = string.Concat(status.Rows[1].Segments.Select(s => s.Text));
        Assert.Contains("Thinking", readinessText);
        Assert.DoesNotContain("Idle", readinessText);
    }

    [Fact]
    public void SetReadiness_WithoutModel_ReadinessRowOmitsModel()
    {
        FakeTerminal fake = new() { Size = (80, 24) };
        TerminalRenderer renderer = new(fake);

        renderer.SetReadiness("0.9.0");

        StatusSet status = Assert.IsType<StatusSet>(Assert.Single(fake.Calls));
        Assert.Equal(2, status.Rows.Count);
        string readinessText = string.Concat(status.Rows[1].Segments.Select(s => s.Text));
        Assert.Contains("[Ready]", readinessText);
        Assert.Contains("v0.9.0", readinessText);
        // No model yet — must not appear.
        Assert.DoesNotContain("claude", readinessText);
    }

    // ── 5b. Preamble and prompt prefix ────────────────────────────────────────

    [Fact]
    public void SetPreamble_SetsPreambleRowContainingDmonLabel()
    {
        FakeTerminal fake = new() { Size = (80, 24) };
        TerminalRenderer renderer = new(fake);

        renderer.SetPreamble();

        InputPreambleSet preamble = Assert.IsType<InputPreambleSet>(Assert.Single(fake.Calls));
        Dcli.Line preambleRow = Assert.Single(preamble.Rows);
        string text = string.Concat(preambleRow.Segments.Select(s => s.Text));
        Assert.Contains("dmon", text);
        Assert.Contains("─", text);
    }

    [Fact]
    public void SetPromptPrefix_SetsPromptWithChevronGlyph()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.SetPromptPrefix();

        InputSetPromptLine call = Assert.IsType<InputSetPromptLine>(Assert.Single(fake.Calls));
        string text = string.Concat(call.Line.Segments.Select(s => s.Text));
        Assert.Contains("❯", text);
    }

    // ── 5c. Welcome banner ───────────────────────────────────────────────────

    [Fact]
    public void PrintWelcome_AppendsMultipleScrollbackLines()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.PrintWelcome();

        IReadOnlyList<FakeCall> calls = fake.Calls;
        // Must emit at least 2 lines: the banner + the tagline.
        Assert.True(calls.Count >= 2);
        Assert.All(calls, c => Assert.IsType<ScrollbackAppendLine>(c));
    }

    [Fact]
    public void PrintWelcome_TaglineContainsDotNetNativeCodingAgent()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.PrintWelcome();

        IEnumerable<string> texts = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => string.Concat(c.Line.Segments.Select(s => s.Text)));

        // At least one line must be the tagline.
        Assert.Contains(texts, t => t.Contains("coding agent"));
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

    // ── 8. SettleTurn markdown rendering ─────────────────────────────────────

    [Fact]
    public void SettleTurn_WithStreamedRawText_CallsSetContentBeforeCommit()
    {
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.AppendToken("# Hello\n");
        renderer.SettleTurn("# Hello\n");

        // Call log: LiveBegun, LiveAppendText, LiveSetContent, LiveCommitted.
        Assert.Equal(4, fake.Calls.Count);
        LiveBegun begun = Assert.IsType<LiveBegun>(fake.Calls[0]);
        Assert.IsType<LiveAppendText>(fake.Calls[1]);
        LiveSetContent setContent = Assert.IsType<LiveSetContent>(fake.Calls[2]);
        LiveCommitted committed = Assert.IsType<LiveCommitted>(fake.Calls[3]);

        // All three post-stream calls share the same block id.
        Assert.Equal(begun.BlockId, setContent.BlockId);
        Assert.Equal(begun.BlockId, committed.BlockId);

        // SetContent carries the markdown-rendered lines; heading text is "Hello".
        Assert.True(setContent.Lines.Count >= 1);
        string firstLineText = string.Concat(setContent.Lines[0].Segments.Select(s => s.Text));
        Assert.Equal("Hello", firstLineText);
    }

    [Fact]
    public void SettleTurn_NoStreamedTokens_NoSetContent()
    {
        // No AppendToken call — _liveBlock is null — SettleTurn returns early.
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.SettleTurn("anything");

        Assert.Empty(fake.Calls);
        Assert.Empty(fake.Calls.OfType<LiveSetContent>());
        Assert.Empty(fake.Calls.OfType<LiveCommitted>());
    }

    [Fact]
    public void SettleTurn_EmptyMarkdownSource_CommitsWithoutSetContent()
    {
        // Open a live block, then settle with empty markdown.
        // Render("") returns [] → SetContent is skipped; Commit still fires.
        FakeTerminal fake = new();
        TerminalRenderer renderer = new(fake);

        renderer.AppendToken("a");
        renderer.SettleTurn(string.Empty);

        // Calls: LiveBegun, LiveAppendText, LiveCommitted — no LiveSetContent.
        Assert.Equal(3, fake.Calls.Count);
        Assert.IsType<LiveBegun>(fake.Calls[0]);
        Assert.IsType<LiveAppendText>(fake.Calls[1]);
        Assert.IsType<LiveCommitted>(fake.Calls[2]);
        Assert.Empty(fake.Calls.OfType<LiveSetContent>());
    }
}
