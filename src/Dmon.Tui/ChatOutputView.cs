using System.Drawing;
using Markdig;
using Markdig.Syntax;
using Microsoft.Extensions.AI;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace Dmon.Tui;

/// <summary>
/// A <see cref="View"/> that renders the conversation turn list.
/// Supports streaming token append with incremental inline-code-span detection.
/// </summary>
/// <remarks>
/// Thread safety: all public methods and the <c>_blocks</c> list must only be accessed on the
/// Terminal.Gui UI thread. Callers from background tasks (Group 5 and later) must marshal via
/// <c>Application.MainLoop.Invoke</c> before calling any member of this class.
/// </remarks>
internal sealed class ChatOutputView : View
{
    // Bright cyan on black — distinct from normal prose for inline code spans.
    private static readonly Terminal.Gui.Drawing.Attribute CodeSpanAttribute =
        new(ColorName16.BrightCyan, ColorName16.Black);

    private readonly List<TurnBlock> _blocks = [];

    // Total number of content rows across all blocks; kept in sync after every mutation.
    private int _totalContentRows;

    public ChatOutputView()
    {
        CanFocus = false;
        ContentSizeTracksViewport = false;
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Appends a new user-role block and redraws.
    /// </summary>
    public void AddUserTurn(string message)
    {
        _blocks.Add(new TurnBlock(ChatRole.User, message));
        RefreshLayout();
        ScrollToBottom();
        SetNeedsDraw();
    }

    /// <summary>
    /// Appends a new empty assistant-role block. Call once at turn start.
    /// </summary>
    public void BeginAssistantTurn()
    {
        _blocks.Add(new TurnBlock(ChatRole.Assistant));
        RefreshLayout();
        ScrollToBottom();
        SetNeedsDraw();
    }

    /// <summary>
    /// Appends <paramref name="token"/> to the last assistant block, scans for
    /// completed inline code spans, and redraws.
    /// </summary>
    public void AppendToken(string token)
    {
        TurnBlock? last = _blocks.LastOrDefault(b => b.Role == ChatRole.Assistant);
        if (last is null)
        {
            // No assistant block yet — create one defensively.
            BeginAssistantTurn();
            last = _blocks.Last();
        }

        last.AppendText(token);
        ScanCodeSpans(last);
        RefreshLayout();
        ScrollToBottom();
        SetNeedsDraw();
    }

    /// <summary>
    /// Parses the last non-rendered assistant block's <see cref="TurnBlock.RawText"/> with
    /// Markdig, builds <see cref="TurnBlock.RenderedText"/> and
    /// <see cref="TurnBlock.RenderedAttributes"/>, sets <see cref="TurnBlock.Rendered"/>
    /// to <see langword="true"/>, then refreshes layout and redraws.
    /// </summary>
    public void SettleTurn()
    {
        TurnBlock? block = _blocks.LastOrDefault(b => b.Role == ChatRole.Assistant && !b.Rendered);
        if (block is not null)
        {
            MarkdownDocument doc = Markdown.Parse(block.RawText);
            List<(string Text, Terminal.Gui.Drawing.Attribute Style)> segments = MarkdownRenderer.Render(doc);

            // Build flat text and parallel attribute array from segments.
            System.Text.StringBuilder sb = new();
            List<Terminal.Gui.Drawing.Attribute> attrs = [];
            foreach ((string text, Terminal.Gui.Drawing.Attribute style) in segments)
            {
                sb.Append(text);
                for (int i = 0; i < text.Length; i++)
                    attrs.Add(style);
            }

            block.RenderedText = sb.ToString();
            block.RenderedAttributes = [.. attrs];
            block.Rendered = true;
        }

        RefreshLayout();
        SetNeedsDraw();
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    protected override bool OnDrawingContent(DrawContext? context)
    {
        Rectangle viewport = Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
            return false;

        // Move() / AddStr() / AddRune() operate in viewport coordinates.
        // The framework does NOT auto-translate by Viewport.Location.Y, so we
        // must compute viewportRow = contentRow - Viewport.Location.Y and skip
        // rows that fall outside the visible window.
        int contentRow = 0;

        foreach (TurnBlock block in _blocks)
        {
            string displayText = (block.Rendered ? block.RenderedText : null) ?? block.RawText;
            string[] lines = SplitToLines(displayText, viewport.Width);

            foreach (string line in lines)
            {
                int viewportRow = contentRow - viewport.Y;
                if (viewportRow < 0 || viewportRow >= viewport.Height)
                {
                    contentRow++;
                    continue;
                }

                DrawBlockLine(block, line, contentRow, viewportRow, viewport.Width);
                contentRow++;
            }
        }

        return true;
    }

    private void DrawBlockLine(TurnBlock block, string line, int contentRow, int viewportRow, int viewportWidth)
    {
        bool isUser = block.Role == ChatRole.User;

        // Prefix: "You: " for user, "     " indent for assistant.
        string prefix = isUser ? "You: " : "     ";
        int prefixLen = prefix.Length;

        Move(0, viewportRow);

        if (isUser)
        {
            // User lines in default attribute — no code span detection.
            AddStr(prefix + TruncateLine(line, viewportWidth - prefixLen));
            return;
        }

        AddStr(prefix);

        Terminal.Gui.Drawing.Attribute normal = GetAttributeForRole(VisualRole.Normal);

        if (block.Rendered && block.RenderedText is not null && block.RenderedAttributes is not null)
        {
            // Settled block: use per-character attributes from the rendered attribute array.
            string displayText = block.RenderedText;
            int lineStartInBlock = ComputeLineStartOffset(displayText, contentRow - BlockStartRow(block), viewportWidth);

            for (int i = 0; i < line.Length && (prefixLen + i) < viewportWidth; i++)
            {
                int charIndex = lineStartInBlock + i;
                Terminal.Gui.Drawing.Attribute attr =
                    charIndex < block.RenderedAttributes.Length
                        ? block.RenderedAttributes[charIndex]
                        : normal;
                SetAttribute(attr);
                AddRune(line[i]);
            }
        }
        else
        {
            // Streaming block: draw character by character, applying CodeSpanAttribute to detected ranges.
            int lineStartInBlock = ComputeLineStartOffset(block.RawText, contentRow - BlockStartRow(block), viewportWidth);

            for (int i = 0; i < line.Length && (prefixLen + i) < viewportWidth; i++)
            {
                int charIndexInBlock = lineStartInBlock + i;
                bool inCodeSpan = IsInCodeSpan(block, charIndexInBlock);

                if (inCodeSpan)
                    SetAttribute(CodeSpanAttribute);
                else
                    SetAttribute(normal);

                AddRune(line[i]);
            }
        }

        // Reset to normal after each line.
        SetAttribute(normal);
    }

    // ------------------------------------------------------------------
    // Inline code span detection (task 2.4)
    // ------------------------------------------------------------------

    /// <summary>
    /// Scans the tail of <see cref="TurnBlock.RawText"/> for newly completed backtick spans.
    /// Only characters from <see cref="TurnBlock.ScannedUpTo"/> onward are examined.
    /// Completed spans are added to <see cref="TurnBlock.CodeSpanRanges"/>.
    /// </summary>
    private static void ScanCodeSpans(TurnBlock block)
    {
        string text = block.RawText;
        int start = block.ScannedUpTo;

        // We need to re-examine from the last open backtick (if any) so that
        // a span opened before ScannedUpTo and closed after it is captured.
        // Walk back to find any unmatched opening backtick.
        int openTick = -1;
        for (int i = start - 1; i >= 0; i--)
        {
            if (text[i] == '`')
            {
                // Check this tick is not already the end of a known range.
                bool alreadyClosed = block.CodeSpanRanges.Any(r => i >= r.Start && i < r.Start + r.Length);
                if (!alreadyClosed)
                {
                    openTick = i;
                    break;
                }
            }
        }

        // Scan from openTick (or start) forward looking for paired backticks.
        int scanFrom = openTick >= 0 ? openTick : start;

        for (int i = scanFrom; i < text.Length; i++)
        {
            if (text[i] != '`')
                continue;

            // Find the closing backtick after this position.
            int closingTick = text.IndexOf('`', i + 1);
            if (closingTick < 0)
                break; // No closing tick yet — stop, will re-scan when more tokens arrive.

            int spanStart = i;
            int spanLength = closingTick - i + 1; // includes both backticks

            // Avoid duplicates.
            bool already = block.CodeSpanRanges.Any(r => r.Start == spanStart);
            if (!already)
                block.CodeSpanRanges.Add((spanStart, spanLength));

            // Jump past the closing tick.
            i = closingTick;
        }

        block.ScannedUpTo = text.Length;
    }

    private static bool IsInCodeSpan(TurnBlock block, int charIndex)
    {
        foreach ((int start, int length) in block.CodeSpanRanges)
        {
            // Exclude the backtick delimiters themselves from the coloured range
            // so only the content between them is highlighted.
            if (charIndex > start && charIndex < start + length - 1)
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Layout helpers
    // ------------------------------------------------------------------

    private void RefreshLayout()
    {
        int width = Viewport.Width > 0 ? Viewport.Width : 80;
        _totalContentRows = _blocks.Sum(b =>
            SplitToLines((b.Rendered ? b.RenderedText : null) ?? b.RawText, width).Length);
        SetContentSize(new Size(width, _totalContentRows));
    }

    /// <summary>
    /// Scrolls the viewport so the last content row is visible.
    /// <see cref="RefreshLayout"/> must have been called first; this method reads
    /// <c>_totalContentRows</c> directly and assumes it is current.
    /// </summary>
    private void ScrollToBottom()
    {
        int visibleRows = Viewport.Height;
        int scrollY = Math.Max(0, _totalContentRows - visibleRows);
        Viewport = new Rectangle(Viewport.X, scrollY, Viewport.Width, Viewport.Height);
    }

    /// <summary>
    /// Returns the content-row index at which <paramref name="block"/> starts.
    /// </summary>
    private int BlockStartRow(TurnBlock block)
    {
        int width = Viewport.Width > 0 ? Viewport.Width : 80;
        int row = 0;
        foreach (TurnBlock b in _blocks)
        {
            if (ReferenceEquals(b, block))
                return row;
            string displayText = (b.Rendered ? b.RenderedText : null) ?? b.RawText;
            row += SplitToLines(displayText, width).Length;
        }
        return row;
    }

    /// <summary>
    /// Given a line index within a block and the viewport width, computes the
    /// start character offset of that line within the block's <see cref="TurnBlock.RawText"/>.
    /// </summary>
    private static int ComputeLineStartOffset(string text, int lineIndex, int width)
    {
        if (lineIndex <= 0)
            return 0;

        // SplitToLines splits on '\n' and also word-wraps within each paragraph.
        // We track whether each display line is the last chunk of a paragraph so we
        // can add back the '\n' that Split consumed when moving to the next paragraph.
        if (width <= 0)
            width = 80;

        int offset = 0;
        int displayLine = 0;
        foreach (string paragraph in text.Split('\n'))
        {
            if (displayLine >= lineIndex)
                break;

            if (paragraph.Length == 0)
            {
                // Empty paragraph: one blank display line, consumed one '\n'.
                // (The enclosing foreach already breaks when displayLine >= lineIndex,
                //  so no inner guard is needed here.)
                offset += 1; // the '\n' itself
                displayLine++;
                continue;
            }

            int pos = 0;
            while (pos < paragraph.Length)
            {
                if (displayLine >= lineIndex)
                    break;

                int take = Math.Min(width, paragraph.Length - pos);
                bool isLastChunk = pos + take >= paragraph.Length;

                offset += take;
                if (isLastChunk)
                    offset += 1; // account for the '\n' that Split consumed

                pos += take;
                displayLine++;
            }
        }

        return offset;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into display lines no wider than
    /// <paramref name="width"/>, respecting newline characters.
    /// </summary>
    private static string[] SplitToLines(string text, int width)
    {
        if (string.IsNullOrEmpty(text))
            return [""];

        if (width <= 0)
            width = 80;

        List<string> lines = [];
        foreach (string paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add("");
                continue;
            }

            int pos = 0;
            while (pos < paragraph.Length)
            {
                int take = Math.Min(width, paragraph.Length - pos);
                lines.Add(paragraph.Substring(pos, take));
                pos += take;
            }
        }

        return [.. lines];
    }

    private static string TruncateLine(string line, int maxLen)
    {
        if (maxLen <= 0) return string.Empty;
        return line.Length <= maxLen ? line : line[..maxLen];
    }
}
