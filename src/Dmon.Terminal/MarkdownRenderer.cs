using Dcli;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Dmon.Terminal;

internal static class MarkdownRenderer
{
    private static readonly Style HeadingStyle =
        new(Format: Format.Bold | Format.Underline);

    private static readonly Style CodeStyle =
        new(Foreground: Color.Named(Color.AnsiColor.Yellow), Format: Format.Bold);

    private static readonly Style CodeBorderStyle =
        new(Foreground: Color.Named(Color.AnsiColor.BrightBlack));

    private static readonly Style LangLabelStyle =
        new(Format: Format.Italic | Format.Dim);

    private static readonly Style LinkStyle =
        new(Foreground: Color.Named(Color.AnsiColor.Blue), Format: Format.Underline);

    private static readonly Style BulletStyle =
        new(Format: Format.Dim);

    public static IReadOnlyList<Line> Render(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return [];

        MarkdownDocument doc = Markdown.Parse(markdown);
        List<Line> lines = [];
        foreach (Block block in doc)
            RenderBlock(block, lines, indent: 0);
        return lines;
    }

    private static void RenderBlock(Block block, List<Line> lines, int indent)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                LineAccumulator acc = new();
                RenderInlines(heading.Inline, acc);
                // Re-apply heading style to all accumulated segments.
                Line headingLine = new(acc.Finish().SelectMany(l => l.Segments)
                    .Select(s => new Segment(s.Text, HeadingStyle)));
                lines.Add(headingLine);
                break;
            }

            case ParagraphBlock paragraph:
            {
                LineAccumulator acc = new();
                RenderInlines(paragraph.Inline, acc);
                lines.AddRange(acc.Finish());
                break;
            }

            case FencedCodeBlock fenced:
            {
                string lang = fenced.Info ?? string.Empty;
                if (!string.IsNullOrEmpty(lang))
                    lines.Add(Line.FromText(lang, LangLabelStyle));
                RenderCodeLines(fenced.Lines.ToString(), lines);
                break;
            }

            case CodeBlock indented:
                RenderCodeLines(indented.Lines.ToString(), lines);
                break;

            case ListBlock list:
                foreach (Block child in list)
                    RenderBlock(child, lines, indent);
                break;

            case ListItemBlock listItem:
            {
                string bulletPrefix = new string(' ', indent * 2) + "  • ";
                foreach (Block child in listItem)
                {
                    if (child is ParagraphBlock para)
                    {
                        LineAccumulator acc = new();
                        RenderInlines(para.Inline, acc);
                        IReadOnlyList<Line> paraLines = acc.Finish();
                        for (int i = 0; i < paraLines.Count; i++)
                        {
                            string prefix = i == 0 ? bulletPrefix : new string(' ', bulletPrefix.Length);
                            IEnumerable<Segment> segments = new Segment[] { new(prefix, BulletStyle) }
                                .Concat(paraLines[i].Segments);
                            lines.Add(new Line(segments));
                        }
                    }
                    else if (child is ListBlock nestedList)
                    {
                        foreach (Block nestedChild in nestedList)
                            RenderBlock(nestedChild, lines, indent + 1);
                    }
                    else
                    {
                        RenderBlock(child, lines, indent);
                    }
                }
                break;
            }

            case ContainerBlock container:
                foreach (Block child in container)
                    RenderBlock(child, lines, indent);
                break;

            default:
            {
                string raw = block.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(raw))
                    lines.Add(Line.FromText(raw));
                break;
            }
        }
    }

    private static void RenderCodeLines(string content, List<Line> lines)
    {
        string[] codeLines = content.TrimEnd('\n', '\r').Split('\n');
        foreach (string codeLine in codeLines)
        {
            lines.Add(new Line(new Segment[]
            {
                new(" │ ", CodeBorderStyle),
                new(codeLine, CodeStyle),
            }));
        }
    }

    private static void RenderInlines(ContainerInline? container, LineAccumulator acc)
    {
        if (container is null) return;

        foreach (Inline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    acc.AppendSegment(new Segment(literal.Content.ToString(), default));
                    break;

                case EmphasisInline emphasis:
                {
                    Format fmt = emphasis.DelimiterCount == 2 ? Format.Bold : Format.Italic;
                    Style emphasisStyle = new(Format: fmt);
                    // Collect inner text via a sub-accumulator, then re-style each segment.
                    LineAccumulator inner = new();
                    RenderInlines(emphasis, inner);
                    IReadOnlyList<Line> innerLines = inner.Finish();
                    for (int i = 0; i < innerLines.Count; i++)
                    {
                        foreach (Segment seg in innerLines[i].Segments)
                            acc.AppendSegment(new Segment(
                                seg.Text,
                                seg.Style with { Format = seg.Style.Format | emphasisStyle.Format }));
                        // Emit a line break between lines but not after the last.
                        if (i < innerLines.Count - 1)
                            acc.BreakLine();
                    }
                    break;
                }

                case CodeInline code:
                    acc.AppendSegment(new Segment(code.Content, CodeStyle));
                    break;

                case LineBreakInline:
                    acc.BreakLine();
                    break;

                case LinkInline link:
                {
                    string linkText = link.FirstChild is LiteralInline lit
                        ? lit.Content.ToString()
                        : link.Url ?? string.Empty;
                    acc.AppendSegment(new Segment(linkText, LinkStyle));
                    break;
                }

                default:
                    if (inline is ContainerInline container2)
                        RenderInlines(container2, acc);
                    break;
            }
        }
    }

    // Accumulates Segments into Lines, splitting on LineBreakInline.
    private sealed class LineAccumulator
    {
        private readonly List<Line> _lines = [];
        private List<Segment> _current = [];

        public void AppendSegment(Segment segment)
        {
            if (segment.Text.Length > 0)
                _current.Add(segment);
        }

        public void BreakLine()
        {
            if (_current.Count > 0)
            {
                _lines.Add(new Line(_current));
                _current = [];
            }
        }

        public IReadOnlyList<Line> Finish()
        {
            if (_current.Count > 0)
            {
                _lines.Add(new Line(_current));
                _current = [];
            }
            return _lines;
        }
    }
}
