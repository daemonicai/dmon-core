using Spectre.Console;

namespace Dmon.Terminal;

internal static class InlinePrompt
{
    /// Prints an interactive scrollable list. Up/Down to navigate, Enter to select.
    /// Returns 0-based index, null for cancel (q / Ctrl+C), -1 for back (b / Escape).
    public static Task<int?> ChooseAsync(
        string title,
        IReadOnlyList<string> options,
        CancellationToken cancellationToken)
    {
        const int MaxVisible = 10;
        int visibleCount = Math.Min(MaxVisible, options.Count);
        int selectedIndex = 0;
        int scrollOffset = 0;

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(title)}[/]");
        PrintList(options, selectedIndex, scrollOffset, visibleCount);

        return Task.Run(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    EraseList(visibleCount, options.Count > visibleCount);
                    return (int?)null;
                }

                switch (key.Key)
                {
                    case ConsoleKey.Q:
                        EraseList(visibleCount, options.Count > visibleCount);
                        return (int?)null;

                    case ConsoleKey.B:
                    case ConsoleKey.Escape:
                        EraseList(visibleCount, options.Count > visibleCount);
                        return (int?)-1;

                    case ConsoleKey.UpArrow:
                        if (selectedIndex > 0)
                        {
                            selectedIndex--;
                            if (selectedIndex < scrollOffset)
                                scrollOffset = selectedIndex;
                            RedrawList(options, selectedIndex, scrollOffset, visibleCount);
                        }
                        break;

                    case ConsoleKey.DownArrow:
                        if (selectedIndex < options.Count - 1)
                        {
                            selectedIndex++;
                            if (selectedIndex >= scrollOffset + visibleCount)
                                scrollOffset = selectedIndex - visibleCount + 1;
                            RedrawList(options, selectedIndex, scrollOffset, visibleCount);
                        }
                        break;

                    case ConsoleKey.Enter:
                        EraseList(visibleCount, options.Count > visibleCount);
                        AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(options[selectedIndex])}[/]");
                        return (int?)selectedIndex;

                    default:
                        if (key.KeyChar == '0')
                        {
                            EraseList(visibleCount, options.Count > visibleCount);
                            return (int?)-1;
                        }
                        break;
                }
            }
            return null;
        }, cancellationToken);
    }

    private static void PrintList(
        IReadOnlyList<string> options,
        int selectedIndex,
        int scrollOffset,
        int visibleCount)
    {
        for (int i = scrollOffset; i < scrollOffset + visibleCount; i++)
        {
            string cursor = i == selectedIndex ? "[green]>[/]" : " ";
            AnsiConsole.MarkupLine($"  {cursor} {Markup.Escape(options[i])}");
        }
        if (options.Count > visibleCount)
            AnsiConsole.MarkupLine($"  [grey]({selectedIndex + 1}/{options.Count})[/]");
        AnsiConsole.MarkupLine("  [grey]↑/↓ navigate · Enter select · b back · q cancel[/]");
    }

    private static void RedrawList(
        IReadOnlyList<string> options,
        int selectedIndex,
        int scrollOffset,
        int visibleCount)
    {
        EraseList(visibleCount, options.Count > visibleCount);
        PrintList(options, selectedIndex, scrollOffset, visibleCount);
    }

    private static void EraseList(int visibleCount, bool hasScrollIndicator)
    {
        int totalLines = visibleCount + (hasScrollIndicator ? 2 : 1);
        Console.Write($"\x1b[{totalLines}A\x1b[J");
    }

    public static Task<string?> ReadLineAsync(
        string prompt,
        bool secret,
        CancellationToken cancellationToken)
    {
        AnsiConsole.Markup($"[grey]{Markup.Escape(prompt)}: [/]");

        return Task.Run(() =>
        {
            var buffer = new System.Text.StringBuilder();

            while (!cancellationToken.IsCancellationRequested)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    return (string?)null;

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return (string?)buffer.ToString();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Remove(buffer.Length - 1, 1);
                        Console.Write("\b \b");
                    }
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                    Console.Write(secret ? '*' : key.KeyChar);
                }
            }
            return null;
        }, cancellationToken);
    }
}
