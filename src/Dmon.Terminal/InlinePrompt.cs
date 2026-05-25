using Spectre.Console;

namespace Dmon.Terminal;

internal static class InlinePrompt
{
    /// Prints a numbered list, reads a single keypress.
    /// Returns 0-based index, or null for cancel (q / Ctrl+C).
    /// Returns -1 when 'b' or '0' is pressed (back sentinel).
    public static Task<int?> ChooseAsync(
        string title,
        IReadOnlyList<string> options,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(title)}[/]");
        for (int i = 0; i < options.Count; i++)
            AnsiConsole.MarkupLine($"  [bold]{i + 1}[/]. {Markup.Escape(options[i])}");
        AnsiConsole.MarkupLine("  [grey]b/0 = back   q = cancel[/]");
        AnsiConsole.Markup("[grey]Choice: [/]");

        return Task.Run(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    return (int?)null;

                char c = char.ToLowerInvariant(key.KeyChar);

                if (c == 'q')
                {
                    Console.WriteLine();
                    return (int?)null;
                }

                if (c == 'b' || c == '0')
                {
                    Console.WriteLine();
                    return (int?)-1;
                }

                if (char.IsDigit(c))
                {
                    int digit = c - '0';
                    if (digit >= 1 && digit <= options.Count)
                    {
                        Console.WriteLine();
                        return (int?)(digit - 1);
                    }
                }
                // Invalid key — ignore and loop
            }
            return null;
        }, cancellationToken);
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
