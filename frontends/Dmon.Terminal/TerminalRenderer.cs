using Dcli;

namespace Dmon.Terminal;

internal sealed class TerminalRenderer
{
    private readonly ITerminal _terminal;
    private string _modelName = string.Empty;
    private string _coreVersion = string.Empty;
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

    public void SettleTurn(string markdownSource)
    {
        if (_liveBlock is null) return;

        IReadOnlyList<Line> lines = MarkdownRenderer.Render(markdownSource);
        if (lines.Count > 0)
            _liveBlock.SetContent(lines);

        _liveBlock.Commit();
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

    /// <summary>
    /// Emits the ASCII banner and tagline MOTD to scrollback at startup.
    /// Call once before the first prompt is shown.
    /// </summary>
    public void PrintWelcome()
    {
        _terminal.Scrollback.Append(Line.FromText("""     _""", GreyStyle));
        _terminal.Scrollback.Append(Line.FromText("""  __| |_ __ ___   ___  _ __""", GreyStyle));
        _terminal.Scrollback.Append(Line.FromText(""" / _` | '_ ` _ \ / _ \| '_ \""", GreyStyle));
        _terminal.Scrollback.Append(Line.FromText("""| (_| | | | | | | (_) | | | |""", GreyStyle));
        _terminal.Scrollback.Append(Line.FromText(""" \__,_|_| |_| |_|\___/|_| |_|""", GreyStyle));
        _terminal.Scrollback.Append(Line.FromText("a .NET-native coding agent", GreyStyle));
    }

    /// <summary>
    /// Pins the <c>── dmon ──</c> rule above the editor via InputPreamble.
    /// Call once at startup — persists across turns.
    /// </summary>
    public void SetPreamble()
    {
        (int columns, _) = _terminal.GetTerminalSize();
        int width = Math.Max(columns, 1);

        const string label = "dmon";
        const string leftRun = "── ";
        const string rightPad = " ";
        int fixedChars = leftRun.Length + label.Length + rightPad.Length;
        int rightRun = Math.Max(0, width - fixedChars);

        Line preambleLine = new(new Segment[]
        {
            new(leftRun, GreyStyle),
            new(label, GreyStyle),
            new(rightPad + new string('─', rightRun), GreyStyle),
        });
        _terminal.InputPreamble.SetRows(preambleLine);
    }

    /// <summary>
    /// Pins the <c>❯ </c> prompt prefix on the editor line via Input.SetPrompt.
    /// Call once at startup — persists across turns and does NOT trigger InputChanged.
    /// </summary>
    public void SetPromptPrefix()
    {
        Line promptLine = new(new Segment[]
        {
            new("❯ ", BoldStyle),
        });
        _terminal.Input.SetPrompt(promptLine);
    }

    /// <summary>
    /// Records the core version for use in the pinned readiness row.
    /// Call at startup (and reload) before the frame is needed.
    /// </summary>
    public void SetReadiness(string coreVersion)
    {
        _coreVersion = coreVersion;
        RefreshStatus();
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
        if (string.IsNullOrEmpty(_coreVersion))
        {
            _terminal.Status.SetRows([]);
            return;
        }

        // Row 1: full-width rule.
        (int columns, _) = _terminal.GetTerminalSize();
        int width = Math.Max(columns, 1);
        Line ruleRow = Line.FromText(new string('─', width), GreyStyle);

        // Row 2: readiness — version + model (when known) + state indicator.
        string indicator = _thinking ? "Thinking…" : "Idle";
        string readinessText = string.IsNullOrEmpty(_modelName)
            ? $"[Ready] dmon core v{_coreVersion} · {indicator}"
            : $"[Ready] dmon core v{_coreVersion} {_modelName} · {indicator}";
        Line readinessRow = Line.FromText(readinessText, GreyStyle);

        _terminal.Status.SetRows([ruleRow, readinessRow]);
    }
}
