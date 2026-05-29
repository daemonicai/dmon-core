using Dcli;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Tier-A pure-function tests for <see cref="MarkdownRenderer"/>.
/// No <c>ITerminal</c> dependency — the renderer is a static transformation.
/// </summary>
public sealed class MarkdownRendererTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static string LineText(Line line) =>
        string.Concat(line.Segments.Select(s => s.Text));

    private static Style? StyleAt(Line line, int segmentIndex) =>
        segmentIndex < line.Segments.Count ? line.Segments[segmentIndex].Style : null;

    // ── empty / null input ─────────────────────────────────────────────────────

    [Fact]
    public void Render_NullInput_ReturnsEmpty()
    {
        // string.IsNullOrEmpty catches null before Markdig is invoked.
        IReadOnlyList<Line> result = MarkdownRenderer.Render(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void Render_EmptyString_ReturnsEmpty()
    {
        IReadOnlyList<Line> result = MarkdownRenderer.Render(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void Render_WhitespaceOnly_ReturnsEmpty()
    {
        // Markdig treats all-blank-line input as containing no blocks.
        IReadOnlyList<Line> result = MarkdownRenderer.Render("   \n  \n");
        Assert.Empty(result);
    }

    // ── plain paragraph ────────────────────────────────────────────────────────

    [Fact]
    public void Render_PlainText_OneLineDefaultStyle()
    {
        IReadOnlyList<Line> result = MarkdownRenderer.Render("hello world");

        Line line = Assert.Single(result);
        Segment seg = Assert.Single(line.Segments);
        Assert.Equal("hello world", seg.Text);
        Assert.Equal(default, seg.Style);
    }

    // ── heading ────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_Heading_BoldUnderline()
    {
        IReadOnlyList<Line> result = MarkdownRenderer.Render("# Title");

        Line line = Assert.Single(result);
        Assert.Equal("Title", LineText(line));
        // All segments carry HeadingStyle — Bold | Underline, no foreground.
        Assert.All(line.Segments, seg =>
        {
            Assert.True(seg.Style.Format.HasFlag(Format.Bold));
            Assert.True(seg.Style.Format.HasFlag(Format.Underline));
            Assert.Null(seg.Style.Foreground);
        });
    }

    [Fact]
    public void Render_Heading_H2_SameStyle()
    {
        // All heading levels are rendered with the same HeadingStyle.
        IReadOnlyList<Line> result = MarkdownRenderer.Render("## Subtitle");

        Line line = Assert.Single(result);
        Assert.Equal("Subtitle", LineText(line));
        Assert.All(line.Segments, seg =>
        {
            Assert.True(seg.Style.Format.HasFlag(Format.Bold));
            Assert.True(seg.Style.Format.HasFlag(Format.Underline));
        });
    }

    // ── inline emphasis ────────────────────────────────────────────────────────

    [Fact]
    public void Render_BoldEmphasis_BoldFormat()
    {
        // "a **bold** b" produces three segments.
        IReadOnlyList<Line> result = MarkdownRenderer.Render("a **bold** b");

        Line line = Assert.Single(result);
        Assert.Equal(3, line.Segments.Count);

        // First segment: "a " with default style.
        Assert.Equal("a ", line.Segments[0].Text);
        Assert.Equal(default, line.Segments[0].Style);

        // Middle segment: "bold" with Bold.
        Assert.Equal("bold", line.Segments[1].Text);
        Assert.Equal(Format.Bold, line.Segments[1].Style.Format);
        Assert.Null(line.Segments[1].Style.Foreground);

        // Last segment: " b" with default style.
        Assert.Equal(" b", line.Segments[2].Text);
        Assert.Equal(default, line.Segments[2].Style);
    }

    [Fact]
    public void Render_ItalicEmphasis_ItalicFormat()
    {
        IReadOnlyList<Line> result = MarkdownRenderer.Render("a *italic* b");

        Line line = Assert.Single(result);
        Assert.Equal(3, line.Segments.Count);

        Assert.Equal("a ", line.Segments[0].Text);
        Assert.Equal(default, line.Segments[0].Style);

        Assert.Equal("italic", line.Segments[1].Text);
        Assert.Equal(Format.Italic, line.Segments[1].Style.Format);
        Assert.Null(line.Segments[1].Style.Foreground);

        Assert.Equal(" b", line.Segments[2].Text);
        Assert.Equal(default, line.Segments[2].Style);
    }

    // ── nested emphasis ────────────────────────────────────────────────────────

    [Fact]
    public void Render_NestedEmphasis_BoldPlusItalic_Compounds()
    {
        // "**bold *and italic***" — outer bold wraps inner italic.
        // Markdig produces: EmphasisInline(2) containing LiteralInline("bold ")
        // and EmphasisInline(1) containing LiteralInline("and italic").
        IReadOnlyList<Line> result = MarkdownRenderer.Render("**bold *and italic***");

        Line line = Assert.Single(result);
        Assert.Equal("bold and italic", LineText(line));

        Segment outerOnly = line.Segments.Single(s => s.Text == "bold ");
        Assert.Equal(Format.Bold, outerOnly.Style.Format);
        Assert.False(outerOnly.Style.Format.HasFlag(Format.Italic));

        Segment inner = line.Segments.Single(s => s.Text == "and italic");
        Assert.True(inner.Style.Format.HasFlag(Format.Bold));
        Assert.True(inner.Style.Format.HasFlag(Format.Italic));
    }

    [Fact]
    public void Render_NestedEmphasis_ItalicPlusBold_Compounds()
    {
        // "*italic **and bold***" — outer italic wraps inner bold.
        IReadOnlyList<Line> result = MarkdownRenderer.Render("*italic **and bold***");

        Line line = Assert.Single(result);
        Assert.Equal("italic and bold", LineText(line));

        Segment outerOnly = line.Segments.Single(s => s.Text == "italic ");
        Assert.Equal(Format.Italic, outerOnly.Style.Format);
        Assert.False(outerOnly.Style.Format.HasFlag(Format.Bold));

        Segment inner = line.Segments.Single(s => s.Text == "and bold");
        Assert.True(inner.Style.Format.HasFlag(Format.Italic));
        Assert.True(inner.Style.Format.HasFlag(Format.Bold));
    }

    [Fact]
    public void Render_TripleEmphasis_BoldAndItalic()
    {
        // "***triple***" — Markdig parses as nested EmphasisInline (count 2 wrapping count 1).
        IReadOnlyList<Line> result = MarkdownRenderer.Render("***triple***");

        Line line = Assert.Single(result);
        Assert.Equal("triple", LineText(line));

        // All segments for the content must carry both Bold and Italic.
        Assert.All(line.Segments, seg =>
        {
            Assert.True(seg.Style.Format.HasFlag(Format.Bold));
            Assert.True(seg.Style.Format.HasFlag(Format.Italic));
        });
    }

    // ── inline code ────────────────────────────────────────────────────────────

    [Fact]
    public void Render_InlineCode_YellowBoldNoBackground()
    {
        IReadOnlyList<Line> result = MarkdownRenderer.Render("a `code` b");

        Line line = Assert.Single(result);
        Assert.Equal(3, line.Segments.Count);

        Assert.Equal("a ", line.Segments[0].Text);
        Assert.Equal(default, line.Segments[0].Style);

        // Inline code uses CodeStyle: Yellow foreground + Bold, no background.
        Assert.Equal("code", line.Segments[1].Text);
        Assert.Equal(Color.Named(Color.AnsiColor.Yellow), line.Segments[1].Style.Foreground);
        Assert.True(line.Segments[1].Style.Format.HasFlag(Format.Bold));
        Assert.Null(line.Segments[1].Style.Background);

        Assert.Equal(" b", line.Segments[2].Text);
        Assert.Equal(default, line.Segments[2].Style);
    }

    // ── fenced code block ──────────────────────────────────────────────────────

    [Fact]
    public void Render_FencedCode_LanguageLabelDimItalic()
    {
        IReadOnlyList<Line> result = MarkdownRenderer.Render("```csharp\nint x = 1;\n```");

        // First line: language label with Italic | Dim.
        Assert.True(result.Count >= 2);
        Line labelLine = result[0];
        Assert.Equal("csharp", LineText(labelLine));
        Segment labelSeg = Assert.Single(labelLine.Segments);
        Assert.True(labelSeg.Style.Format.HasFlag(Format.Italic));
        Assert.True(labelSeg.Style.Format.HasFlag(Format.Dim));
        Assert.Null(labelSeg.Style.Foreground);

        // Subsequent lines: " │ " border (BrightBlack) + code text (Yellow + Bold).
        Line codeLine = result[1];
        Assert.Equal(2, codeLine.Segments.Count);
        Segment border = codeLine.Segments[0];
        Segment code = codeLine.Segments[1];

        Assert.Equal(" │ ", border.Text);
        Assert.Equal(Color.Named(Color.AnsiColor.BrightBlack), border.Style.Foreground);

        Assert.Equal("int x = 1;", code.Text);
        Assert.Equal(Color.Named(Color.AnsiColor.Yellow), code.Style.Foreground);
        Assert.True(code.Style.Format.HasFlag(Format.Bold));
    }

    [Fact]
    public void Render_FencedCode_NoLanguage_OmitsLabel()
    {
        IReadOnlyList<Line> result = MarkdownRenderer.Render("```\nfoo\n```");

        // No language label — only the single code line.
        Line codeLine = Assert.Single(result);
        Assert.Equal(2, codeLine.Segments.Count);
        Assert.Equal(" │ ", codeLine.Segments[0].Text);
        Assert.Equal("foo", codeLine.Segments[1].Text);
    }

    [Fact]
    public void Render_FencedCode_MultipleLines_OneLinePerCodeLine()
    {
        IReadOnlyList<Line> result = MarkdownRenderer.Render("```\na\nb\nc\n```");

        // Three code lines (no language label).
        Assert.Equal(3, result.Count);
        Assert.Equal("a", result[0].Segments[1].Text);
        Assert.Equal("b", result[1].Segments[1].Text);
        Assert.Equal("c", result[2].Segments[1].Text);
    }

    // ── indented code block ────────────────────────────────────────────────────

    [Fact]
    public void Render_IndentedCode_NoLanguageLabel()
    {
        // Four-space indented code block — Markdig recognises this as an indented CodeBlock.
        IReadOnlyList<Line> result = MarkdownRenderer.Render("    foo bar");

        // No language label; just the single code line.
        Line codeLine = Assert.Single(result);
        Assert.Equal(2, codeLine.Segments.Count);

        Segment border = codeLine.Segments[0];
        Segment code = codeLine.Segments[1];

        Assert.Equal(" │ ", border.Text);
        Assert.Equal(Color.Named(Color.AnsiColor.BrightBlack), border.Style.Foreground);

        Assert.Equal("foo bar", code.Text);
        Assert.Equal(Color.Named(Color.AnsiColor.Yellow), code.Style.Foreground);
        Assert.True(code.Style.Format.HasFlag(Format.Bold));
    }

    // ── lists ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_UnorderedList_BulletAndIndent()
    {
        IReadOnlyList<Line> result = MarkdownRenderer.Render("- a\n- b");

        Assert.Equal(2, result.Count);

        // Each line: first segment is the bullet prefix with Dim style.
        foreach (Line line in result)
        {
            Assert.True(line.Segments.Count >= 2);
            Segment bullet = line.Segments[0];
            // Bullet prefix is "  • " at indent=0.
            Assert.Equal("  • ", bullet.Text);
            Assert.Equal(Format.Dim, bullet.Style.Format);
            Assert.Null(bullet.Style.Foreground);
        }

        // Item text segments follow the bullet.
        string firstItem = string.Concat(result[0].Segments.Skip(1).Select(s => s.Text));
        string secondItem = string.Concat(result[1].Segments.Skip(1).Select(s => s.Text));
        Assert.Equal("a", firstItem);
        Assert.Equal("b", secondItem);
    }

    [Fact]
    public void Render_OrderedList_SameStyleAsUnordered()
    {
        // The renderer treats ordered and unordered ListItemBlock the same —
        // same "  • " bullet prefix, same Dim style.
        IReadOnlyList<Line> result = MarkdownRenderer.Render("1. a\n2. b");

        Assert.Equal(2, result.Count);

        foreach (Line line in result)
        {
            Assert.True(line.Segments.Count >= 2);
            Segment bullet = line.Segments[0];
            Assert.Equal("  • ", bullet.Text);
            Assert.Equal(Format.Dim, bullet.Style.Format);
        }

        string firstItem = string.Concat(result[0].Segments.Skip(1).Select(s => s.Text));
        string secondItem = string.Concat(result[1].Segments.Skip(1).Select(s => s.Text));
        Assert.Equal("a", firstItem);
        Assert.Equal("b", secondItem);
    }

    // ── link ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_Link_UnderlineBlue()
    {
        // URL is dropped; link text rendered with LinkStyle (Blue + Underline).
        IReadOnlyList<Line> result = MarkdownRenderer.Render("[home](https://example.com)");

        Line line = Assert.Single(result);
        // Empty segments are suppressed by the LineAccumulator (Text.Length > 0 guard),
        // so the link is the only segment when there is no surrounding text.
        Segment linkSeg = Assert.Single(line.Segments);
        Assert.Equal("home", linkSeg.Text);
        Assert.Equal(Color.Named(Color.AnsiColor.Blue), linkSeg.Style.Foreground);
        Assert.True(linkSeg.Style.Format.HasFlag(Format.Underline));
    }

    // ── hard line break ────────────────────────────────────────────────────────

    [Fact]
    public void Render_HardLineBreak_SplitsIntoMultipleLines()
    {
        // Two trailing spaces before \n force a Markdig LineBreakInline (hard break).
        IReadOnlyList<Line> result = MarkdownRenderer.Render("line one  \nline two");

        Assert.Equal(2, result.Count);
        Assert.Equal("line one", LineText(result[0]));
        Assert.Equal("line two", LineText(result[1]));
    }

    // ── purity ────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_Pure_DeterministicAndNoSideEffects()
    {
        const string markdown = "# Hello";

        IReadOnlyList<Line> first = MarkdownRenderer.Render(markdown);
        IReadOnlyList<Line> second = MarkdownRenderer.Render(markdown);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Segments.Count, second[i].Segments.Count);
            for (int j = 0; j < first[i].Segments.Count; j++)
            {
                Assert.Equal(first[i].Segments[j].Text,  second[i].Segments[j].Text);
                Assert.Equal(first[i].Segments[j].Style, second[i].Segments[j].Style);
            }
        }
    }
}
