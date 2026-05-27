namespace Dmon.Terminal;

/// <summary>
/// Synchronous console arrow-key picker. Renders a numbered list of items,
/// lets the user navigate with UpArrow/DownArrow, confirm with Enter, or cancel with Escape.
/// Returns the selected item or null if cancelled.
/// Input must already be locked by the caller before invoking Run.
/// </summary>
internal static class ConsolePicker
{
    public static string? Run(IReadOnlyList<string> items, int preSelectIndex = 0)
    {
        if (items.Count == 0) return null;

        int selected = Math.Clamp(preSelectIndex, 0, items.Count - 1);
        Render(items, selected);

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    selected = selected > 0 ? selected - 1 : items.Count - 1;
                    Rerender(items, selected);
                    break;

                case ConsoleKey.DownArrow:
                    selected = selected < items.Count - 1 ? selected + 1 : 0;
                    Rerender(items, selected);
                    break;

                case ConsoleKey.Enter:
                    ClearPicker(items.Count);
                    return items[selected];

                case ConsoleKey.Escape:
                    ClearPicker(items.Count);
                    return null;
            }
        }
    }

    private static void Render(IReadOnlyList<string> items, int selected)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i == selected)
                Console.WriteLine($"  > {items[i]}");
            else
                Console.WriteLine($"    {items[i]}");
        }
    }

    private static void Rerender(IReadOnlyList<string> items, int selected)
    {
#pragma warning disable CA1416 // Console.CursorTop is supported on all target platforms
        Console.CursorTop = Math.Max(0, Console.CursorTop - items.Count);
#pragma warning restore CA1416
        Render(items, selected);
    }

    private static void ClearPicker(int lineCount)
    {
        for (int i = 0; i < lineCount; i++)
        {
#pragma warning disable CA1416
            Console.CursorTop = Math.Max(0, Console.CursorTop - 1);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.CursorLeft = 0;
#pragma warning restore CA1416
        }
    }
}
