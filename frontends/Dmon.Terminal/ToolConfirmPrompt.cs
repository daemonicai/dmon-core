using Dcli;

namespace Dmon.Terminal;

internal static class ToolConfirmPrompt
{
    private static readonly IReadOnlyList<Line> Options =
    [
        Line.FromText("Allow once"),
        Line.FromText("Allow for this project"),
        Line.FromText("Allow always (global)"),
        Line.FromText("Deny"),
    ];

    public static async Task<ToolPermission?> ShowAsync(
        ITerminal terminal,
        string name,
        string args,
        string risk,
        CancellationToken cancellationToken)
    {
        string truncatedArgs = args.Length > 80 ? args[..77] + "..." : args;

        Line riskLine = string.Equals(risk, "high", StringComparison.OrdinalIgnoreCase)
            ? new LineBuilder()
                .Append("⚠ HIGH RISK", new Style(Foreground: Color.Named(Color.AnsiColor.Red), Format: Format.Bold))
                .Build()
            : new LineBuilder()
                .Fg($"Risk: {risk}", Color.Named(Color.AnsiColor.BrightBlack))
                .Build();

        IReadOnlyList<Line> prompt =
        [
            new LineBuilder().Bold("Tool: ").Text(name).Build(),
            new LineBuilder().Bold("Args: ").Text(truncatedArgs).Build(),
            riskLine,
            Line.FromText("Permission:"),
        ];

        DialogResult<int> result = await terminal.ChoiceAsync(
            new ChoiceRequest(Options: Options, Prompt: prompt),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome != DialogOutcome.Submitted)
            return null;

        return result.Value switch
        {
            0 => ToolPermission.Once,
            1 => ToolPermission.Project,
            2 => ToolPermission.Global,
            _ => null,
        };
    }
}
