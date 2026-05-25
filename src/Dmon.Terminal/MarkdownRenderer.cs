using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;

namespace Dmon.Terminal;

internal static class MarkdownRenderer
{
    public static string Render(string markdown)
    {
        MarkdownDocument doc = Markdown.Parse(markdown);
        var sb = new StringBuilder();
        foreach (Block block in doc)
            RenderBlock(block, sb);
        return sb.ToString();
    }

    private static void RenderBlock(Block block, StringBuilder sb)
    {
        switch (block)
        {
            case HeadingBlock heading:
                sb.Append("[bold underline]");
                sb.Append(RenderInlines(heading.Inline));
                sb.AppendLine("[/]");
                break;

            case ParagraphBlock paragraph:
                sb.Append(RenderInlines(paragraph.Inline));
                sb.Append('\n');
                break;

            case FencedCodeBlock fenced:
            {
                string lang = fenced.Info ?? string.Empty;
                if (!string.IsNullOrEmpty(lang))
                    sb.AppendLine($"[italic dim]{Markup.Escape(lang)}[/]");
                RenderCodeLines(fenced.Lines.ToString(), sb);
                sb.Append('\n');
                break;
            }

            case CodeBlock indented:
                RenderCodeLines(indented.Lines.ToString(), sb);
                sb.Append('\n');
                break;

            case ListBlock list:
                foreach (Block child in list)
                    RenderBlock(child, sb);
                break;

            case ListItemBlock listItem:
                foreach (Block child in listItem)
                {
                    if (child is ParagraphBlock para)
                    {
                        sb.Append("  • ");
                        sb.Append(RenderInlines(para.Inline));
                        sb.Append('\n');
                    }
                    else
                    {
                        RenderBlock(child, sb);
                    }
                }
                break;

            case ContainerBlock container:
                foreach (Block child in container)
                    RenderBlock(child, sb);
                break;

            default:
                string raw = block.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(raw))
                    sb.AppendLine(Markup.Escape(raw));
                break;
        }
    }

    private static void RenderCodeLines(string content, StringBuilder sb)
    {
        string[] lines = content.TrimEnd('\n', '\r').Split('\n');
        foreach (string line in lines)
            sb.AppendLine($"[bold yellow on grey35] │ {Markup.Escape(line)}[/]");
    }

    private static string RenderInlines(ContainerInline? container)
    {
        if (container is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (Inline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(Markup.Escape(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                {
                    string inner = RenderInlines(emphasis);
                    if (emphasis.DelimiterCount == 2)
                        sb.Append($"[bold]{inner}[/]");
                    else
                        sb.Append($"[italic]{inner}[/]");
                    break;
                }

                case CodeInline code:
                    sb.Append($"[bold yellow on grey35]{Markup.Escape(code.Content)}[/]");
                    break;

                case LineBreakInline:
                    sb.Append('\n');
                    break;

                default:
                    if (inline is ContainerInline container2)
                        sb.Append(RenderInlines(container2));
                    break;
            }
        }
        return sb.ToString();
    }
}
