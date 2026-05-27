using Spectre.Console;

namespace Dmon.Terminal;

internal sealed class TerminalRenderer
{
    private readonly Func<string>? _getBuffer;

    private int _currentLineLength;
    private int _streamedLineCount;
    private string _modelName = string.Empty;
    private bool _thinking;
    private bool _promptActive;
    private int _promptBlockLines;

    public TerminalRenderer(Func<string>? getBuffer = null)
    {
        _getBuffer = getBuffer;
    }

    public void AppendToken(string token)
    {
        InterruptPrompt();
        Console.Write(token);
        foreach (char c in token)
        {
            if (c == '\n')
            {
                _streamedLineCount++;
                _currentLineLength = 0;
            }
            else
            {
                _currentLineLength++;
            }
        }
        Console.Out.Flush();
    }

    public void SettleTurn(string spectreMarkup)
    {
        InterruptPrompt();
        Console.Write("\r\x1b[2K");
        for (int i = 0; i < _streamedLineCount; i++)
        {
            Console.Write("\x1b[1A\x1b[2K");
        }

        AnsiConsole.MarkupLine(spectreMarkup);

        _streamedLineCount = 0;
        _currentLineLength = 0;
    }

    public void PrintSeparator(string? label = null)
    {
        bool wasActive = _promptActive;
        InterruptPrompt();
        string ruleLabel = label ?? BuildStatusLabel();
        if (string.IsNullOrEmpty(ruleLabel))
            AnsiConsole.Write(new Rule());
        else
            AnsiConsole.Write(new Rule($"[grey]{Markup.Escape(ruleLabel)}[/]").LeftJustified());
        RestorePromptIfWasActive(wasActive);
    }

    public void PrintPrompt()
    {
        _promptActive = false;
        Console.WriteLine();
        AnsiConsole.Write(new Rule());
        bool hasStatus = !string.IsNullOrEmpty(_modelName);
        if (hasStatus)
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(_modelName)}[/]");
        AnsiConsole.Write(new Rule());
        AnsiConsole.Markup("[bold]❯ [/]");
        _promptBlockLines = hasStatus ? 4 : 3;
        _promptActive = true;
        _currentLineLength = 2;
        _streamedLineCount = 0;
    }

    public void AddUserLine(string text)
    {
        InterruptPrompt();
        AnsiConsole.MarkupLine($"[bold]❯ {Markup.Escape(text)}[/]");
    }

    public void AddSystemLine(string text)
    {
        bool wasActive = _promptActive;
        InterruptPrompt();
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(text)}[/]");
        RestorePromptIfWasActive(wasActive);
    }

    public void SetStatus(string modelName, bool thinking)
    {
        _modelName = modelName;
        _thinking = thinking;
    }

    private void InterruptPrompt()
    {
        if (!_promptActive) return;
        Console.Write("\r\x1b[2K");
        for (int i = 0; i < _promptBlockLines; i++)
            Console.Write("\x1b[1A\x1b[2K");
        _promptActive = false;
    }

    private void RestorePromptIfWasActive(bool wasActive)
    {
        if (!wasActive) return;
        PrintPrompt();
        string buffer = _getBuffer?.Invoke() ?? string.Empty;
        if (buffer.Length > 0)
            Console.Write(buffer);
    }

    private string BuildStatusLabel()
    {
        if (string.IsNullOrEmpty(_modelName)) return string.Empty;
        return _thinking ? $"{_modelName} · thinking…" : _modelName;
    }
}
