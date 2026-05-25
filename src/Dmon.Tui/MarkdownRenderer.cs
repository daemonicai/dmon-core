using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Terminal.Gui.Drawing;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace Dmon.Tui;

/// <summary>
/// Walks a Markdig AST and produces a flat list of (text, attribute) segments
/// suitable for per-character rendering in <see cref="ChatOutputView"/>.
/// </summary>
internal static class MarkdownRenderer
{
    private static readonly Attr NormalAttribute = new(ColorName16.White, ColorName16.Black);
    private static readonly Attr CodeAttribute = new(ColorName16.BrightCyan, ColorName16.Black);
    private static readonly Attr BoldAttribute = new(ColorName16.White, ColorName16.Black, TextStyle.Bold);
    private static readonly Attr ItalicAttribute = new(ColorName16.White, ColorName16.Black, TextStyle.Italic);

    public static List<(string Text, Attr Style)> Render(MarkdownDocument doc)
    {
        List<(string, Attr)> segments = [];
        foreach (Block block in doc)
            RenderBlock(block, segments);
        return segments;
    }

    private static void RenderBlock(Block block, List<(string, Attr)> segments)
    {
        switch (block)
        {
            case HeadingBlock heading:
                RenderInlines(heading.Inline, segments, BoldAttribute);
                segments.Add(("\n\n", BoldAttribute));
                break;

            case ParagraphBlock paragraph:
                RenderInlines(paragraph.Inline, segments, NormalAttribute);
                segments.Add(("\n\n", NormalAttribute));
                break;

            case FencedCodeBlock fenced:
                RenderCodeLines(fenced.Lines.ToString(), segments);
                segments.Add(("\n", CodeAttribute));
                break;

            case CodeBlock indented:
                RenderCodeLines(indented.Lines.ToString(), segments);
                segments.Add(("\n", CodeAttribute));
                break;

            case ListBlock list:
                foreach (Block item in list)
                    RenderBlock(item, segments);
                break;

            case ListItemBlock listItem:
                // Emit prefix once, then render inline content of child paragraphs.
                segments.Add(("  • ", NormalAttribute));
                foreach (Block child in listItem)
                {
                    if (child is ParagraphBlock para)
                    {
                        RenderInlines(para.Inline, segments, NormalAttribute);
                        segments.Add(("\n", NormalAttribute));
                    }
                    else
                    {
                        RenderBlock(child, segments);
                    }
                }
                break;

            case ContainerBlock container:
                foreach (Block child in container)
                    RenderBlock(child, segments);
                break;

            default:
                string raw = block.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(raw))
                    segments.Add((raw + "\n", NormalAttribute));
                break;
        }
    }

    private static void RenderCodeLines(string content, List<(string, Attr)> segments)
    {
        // content may end with a trailing newline from Markdig; normalise.
        string[] lines = content.TrimEnd('\n', '\r').Split('\n');
        foreach (string line in lines)
            segments.Add(("  │ " + line + "\n", CodeAttribute));
    }

    private static void RenderInlines(
        ContainerInline? container,
        List<(string, Attr)> segments,
        Attr inheritedStyle)
    {
        if (container is null)
            return;

        foreach (Inline inline in container)
            RenderInline(inline, segments, inheritedStyle);
    }

    private static void RenderInline(Inline inline, List<(string, Attr)> segments, Attr inheritedStyle)
    {
        switch (inline)
        {
            case LiteralInline literal:
                segments.Add((literal.Content.ToString(), inheritedStyle));
                break;

            case CodeInline code:
                segments.Add((code.Content, CodeAttribute));
                break;

            case EmphasisInline emphasis:
                // DelimiterCount == 2 → bold; 1 → italic.
                Attr emphasisStyle = emphasis.DelimiterCount >= 2 ? BoldAttribute : ItalicAttribute;
                RenderInlines(emphasis, segments, emphasisStyle);
                break;

            case LineBreakInline:
                segments.Add(("\n", inheritedStyle));
                break;

            case ContainerInline container:
                RenderInlines(container, segments, inheritedStyle);
                break;

            default:
                string text = inline.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(text))
                    segments.Add((text, inheritedStyle));
                break;
        }
    }
}
