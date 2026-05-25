using Spectre.Console;

namespace Dmon.Terminal;

internal sealed class TerminalRenderer
{
    private int _currentLineLength;
    private int _streamedLineCount;
    private string _modelName = string.Empty;
    private bool _thinking;

    public void AppendToken(string token)
    {
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
        string ruleLabel = label ?? BuildStatusLabel();
        if (string.IsNullOrEmpty(ruleLabel))
            AnsiConsole.Write(new Rule());
        else
            AnsiConsole.Write(new Rule($"[grey]{Markup.Escape(ruleLabel)}[/]").LeftJustified());
    }

    public void PrintPrompt()
    {
        AnsiConsole.Markup("[grey] > [/]");
        _currentLineLength = 3;
        _streamedLineCount = 0;
    }

    public void AddUserLine(string text)
    {
        AnsiConsole.MarkupLine($"[bold] > {Markup.Escape(text)}[/]");
    }

    public void AddSystemLine(string text)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(text)}[/]");
    }

    public void SetStatus(string modelName, bool thinking)
    {
        _modelName = modelName;
        _thinking = thinking;
    }

    private string BuildStatusLabel()
    {
        if (string.IsNullOrEmpty(_modelName)) return string.Empty;
        return _thinking ? $"{_modelName} · thinking…" : _modelName;
    }
}
