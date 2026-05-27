# Building a Claude Code–Style CLI in .NET 10

A persistent input area pinned to the bottom of the terminal, with regular scrolling output above it, while the user can type even as background tasks emit log lines.

Good news: you don't need ncurses for this. The effect Claude Code creates is built on a single underused ANSI feature called the **scroll region** (DECSTBM). Once you understand that, the rest is just careful cursor positioning.

---

## The core trick

A terminal normally treats the whole screen as one scrolling buffer: when you write a newline at the bottom, everything shifts up one row. DECSTBM lets you say "only rows 1 through N participate in scrolling — the rows below that are frozen."

So your layout becomes:

```
row 1            ┌─ scroll region (top=1, bottom=H-7) ─┐
...              │ regular log output, scrolls         │
row H-7          └─────────────────────────────────────┘
row H-6          (blank)                                ← frozen
row H-5          ────────────                           ← frozen
row H-4          > user input here                      ← frozen
row H-3          ────────────                           ← frozen
row H-2          status line 0                          ← frozen
row H-1          status line 1                          ← frozen
row H            (blank)                                ← frozen
```

When a background task wants to print a log line, it moves the cursor to the bottom row of the scroll region and writes `text\n`. The newline causes only the scroll region to scroll. The frozen bottom rows don't move.

When the user presses a key, you only repaint the input row.

---

## ANSI codes you'll actually use

```
ESC = \x1b
CSI = \x1b[

CSI t;b r        Set scroll region from row t to row b (1-based)
CSI r;c H        Move cursor to row r, col c
CSI 2K           Erase the entire current line
CSI 2J           Clear screen
CSI ?25l / ?25h  Hide / show cursor
ESC 7  / ESC 8   Save / restore cursor position (no brackets!)
CSI s  / CSI u   Save / restore cursor (alternate, less reliable)
```

The two save/restore pairs behave differently across terminals; `ESC 7` / `ESC 8` is the more portable one.

---

## .NET-specific setup

On Windows you need to enable virtual terminal processing or none of this works. .NET on modern Windows usually does this for you, but be explicit:

```csharp
using System.Runtime.InteropServices;

if (OperatingSystem.IsWindows())
{
    var handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
    GetConsoleMode(handle, out var mode);
    SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
}
Console.OutputEncoding = System.Text.Encoding.UTF8;
```

Also: `Console.WindowHeight` and `Console.WindowWidth` give you the visible viewport. Cache them and watch for resize.

---

## Architecture sketch

Three concerns, kept separate:

1. **Layout** — knows the terminal size, computes which rows belong to the scroll region vs. the chrome.
2. **Renderer** — owns the screen. Only it writes ANSI codes. It has two operations: `AppendLog(string)` and `RepaintChrome(inputBuffer, status0, status1)`.
3. **Input loop** — reads keys, mutates an input buffer, asks the renderer to repaint the chrome.

Critically, the renderer needs a **lock** around its writes. If a log message and a keystroke land at the same time, you don't want the ANSI sequences to interleave.

Here's a working skeleton:

```csharp
public sealed class TerminalUi
{
    private readonly object _gate = new();
    private string _input = "";
    private int _cursor = 0;            // caret position within _input
    private string _status0 = "ready";
    private string _status1 = "";
    private int _height, _width;
    private int _scrollBottom;          // last row of scroll region
    private int _inputRow;              // absolute row for the input line

    public void Start()
    {
        Console.Write("\x1b[2J");                       // clear screen
        Recompute();
        Console.Write($"\x1b[1;{_scrollBottom}r");      // set scroll region
        Console.Write($"\x1b[{_scrollBottom};1H");      // park cursor at bottom of scroll region
        RepaintChrome();
    }

    private void Recompute()
    {
        _height = Console.WindowHeight;
        _width = Console.WindowWidth;
        _scrollBottom = _height - 7;
        _inputRow = _height - 4;
    }

    public void AppendLog(string line)
    {
        lock (_gate)
        {
            // Park at the bottom of the scroll region, write, then put cursor back in input
            Console.Write("\x1b7");                                  // save
            Console.Write($"\x1b[1;{_scrollBottom}r");               // reassert region (paranoia after resize)
            Console.Write($"\x1b[{_scrollBottom};1H");               // bottom of scroll region
            Console.Write(line + "\n");                              // \n scrolls just the region
            Console.Write("\x1b8");                                  // restore
            PositionInputCaret();
        }
    }

    public void RepaintChrome()
    {
        lock (_gate)
        {
            Write(_height - 6, 1, new string(' ', _width));                // blank
            Write(_height - 5, 1, new string('─', _width));                // divider
            Write(_inputRow,  1, $"│ > {_input}".PadRight(_width - 1) + "│");
            Write(_height - 3, 1, new string('─', _width));                // divider
            Write(_height - 2, 1, _status0.PadRight(_width));
            Write(_height - 1, 1, _status1.PadRight(_width));
            Write(_height,     1, new string(' ', _width));                // blank
            PositionInputCaret();
        }
    }

    private void PositionInputCaret()
    {
        // "│ > " is 4 chars, so caret column = 5 + _cursor
        Console.Write($"\x1b[{_inputRow};{5 + _cursor}H");
    }

    private static void Write(int row, int col, string text)
    {
        Console.Write($"\x1b[{row};{col}H\x1b[2K{text}");
    }

    public void SetStatus(int line, string text)
    {
        if (line == 0) _status0 = text; else _status1 = text;
        RepaintChrome();
    }

    public void HandleKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Backspace when _cursor > 0:
                _input = _input.Remove(_cursor - 1, 1); _cursor--; break;
            case ConsoleKey.LeftArrow when _cursor > 0:
                _cursor--; break;
            case ConsoleKey.RightArrow when _cursor < _input.Length:
                _cursor++; break;
            case ConsoleKey.Enter:
                var submitted = _input; _input = ""; _cursor = 0;
                AppendLog($"> {submitted}");
                // dispatch submitted to your app...
                break;
            default:
                if (!char.IsControl(k.KeyChar))
                {
                    _input = _input.Insert(_cursor, k.KeyChar.ToString());
                    _cursor++;
                }
                break;
        }
        RepaintChrome();
    }

    public void Stop()
    {
        lock (_gate)
        {
            Console.Write($"\x1b[1;{_height}r");   // reset scroll region to full screen
            Console.Write($"\x1b[{_height};1H");   // park cursor at bottom
            Console.Write("\x1b[?25h");            // make sure cursor is visible
        }
    }
}
```

And the driving loop:

```csharp
var ui = new TerminalUi();
ui.Start();

// background work feeds logs in
_ = Task.Run(async () =>
{
    int i = 0;
    while (true)
    {
        await Task.Delay(500);
        ui.AppendLog($"event #{i++} at {DateTime.Now:HH:mm:ss}");
    }
});

// foreground input loop
while (true)
{
    var key = Console.ReadKey(intercept: true);  // intercept = don't echo
    if (key.Key == ConsoleKey.Escape) break;
    ui.HandleKey(key);
}

ui.Stop();
```

---

## Things that will bite you

**Resize.** When the terminal resizes, your scroll region is wrong and the input row has moved. There's no portable async resize signal in .NET — poll `Console.WindowHeight`/`WindowWidth` on a timer (every ~100ms is plenty) and trigger a full `Recompute()` + clear + repaint when they change.

**Don't write outside the scroll region accidentally.** Stray `Console.WriteLine` from library code will land wherever the cursor happens to be, scribbling on your chrome. Either redirect `Console.Out` to a `TextWriter` that calls `AppendLog`, or use a real logger and route its output through your renderer.

**Wide characters and emoji.** `_input.Length` is char count, not display columns. If you allow non-ASCII input you need to track display width separately (look at `System.Text.Rune` and `EastAsianWidth` properties). Easy to ignore for v1.

**Alternate screen buffer.** You'll see `\x1b[?1049h` mentioned for "full-screen apps" — it switches to a separate buffer that restores on exit. Claude Code intentionally doesn't use it (so your session output stays in scrollback). For your case, skip it.

**Windows Terminal vs. legacy conhost.** Modern Windows Terminal handles all of this well. The legacy Windows console host has spottier DECSTBM support. If you need to support both, test early.

---

## Library alternatives

If you'd rather not hand-roll this:

- **Spectre.Console** has `Live` displays and layout primitives but is awkward for "scrolling region above, persistent input below" specifically — it wants to own the whole screen.
- **Terminal.Gui** (gui-cs) is a full TUI toolkit, well-suited to this but a heavier dependency and a different programming model.
- **PrettyPrompt** is a great readline replacement (multi-line input, history, syntax highlighting) but doesn't manage the scroll-region split.

For something that looks like Claude Code specifically, hand-rolling with the scroll-region trick is honestly the cleanest path — the scope is small and you keep control over the exact aesthetic. Start with the skeleton above, add resize handling next, then history and Ctrl-key bindings.
