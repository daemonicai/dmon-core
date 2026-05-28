using Dcli;

namespace Dmon.Terminal;

internal sealed class TerminalRenderer
{
    private readonly ITerminal _terminal;
    private string _modelName = string.Empty;
    private bool _thinking;
    private ILiveBlock? _liveBlock;

    // Grey used for system lines, separators, and status indicator text.
    private static readonly Style GreyStyle =
        new(Foreground: Color.Named(Color.AnsiColor.BrightBlack));

    // Bold used for the user-echo prefix.
    private static readonly Style BoldStyle =
        new(Format: Format.Bold);

    public TerminalRenderer(ITerminal terminal)
    {
        _terminal = terminal;
    }

    public void AppendToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return;

        _liveBlock ??= _terminal.Scrollback.BeginLive();
        _liveBlock.AppendText(token);
    }

    // Phase 1 trade-off: the committed live block (raw streamed tokens) IS the final rendered turn.
    // Phase 5 will call SetContent(Line[]) before Commit to restore rich Markdown rendering.
    public void SettleTurn(string spectreMarkup)
    {
        _liveBlock?.Commit();
        _liveBlock = null;
    }

    public void PrintSeparator(string? label = null)
    {
        (int columns, _) = _terminal.GetTerminalSize();
        int width = Math.Max(columns, 1);

        if (string.IsNullOrEmpty(label))
        {
            _terminal.Scrollback.Append(Line.FromText(new string('─', width), GreyStyle));
        }
        else
        {
            // ── grey-styled-label ───────
            // Two dashes + space on each side; fill remainder with ─ on the right.
            const string leftRun = "── ";
            const string rightPad = " ";
            int labelLen = label.Length;
            int fixedChars = leftRun.Length + labelLen + rightPad.Length;
            int rightRun = Math.Max(0, width - fixedChars);

            Line separatorLine = new(new Segment[]
            {
                new(leftRun, GreyStyle),
                new(label, GreyStyle),
                new(rightPad + new string('─', rightRun), GreyStyle),
            });
            _terminal.Scrollback.Append(separatorLine);
        }
    }

    public void AddUserLine(string text)
    {
        Line line = new(new Segment[]
        {
            new("❯ ", BoldStyle),
            new(text),
        });
        _terminal.Scrollback.Append(line);
    }

    public void AddSystemLine(string text)
    {
        _terminal.Scrollback.Append(Line.FromText(text, GreyStyle));
    }

    public void SetStatus(string modelName, bool thinking)
    {
        _modelName = modelName;
        _thinking = thinking;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (string.IsNullOrEmpty(_modelName))
        {
            _terminal.Status.SetRows([]);
            return;
        }

        string label = _thinking ? $"{_modelName} · thinking…" : _modelName;
        _terminal.Status.SetRows(Line.FromText(label, GreyStyle));
    }
}
