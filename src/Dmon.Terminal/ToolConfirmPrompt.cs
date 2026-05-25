using Spectre.Console;

namespace Dmon.Terminal;

internal static class ToolConfirmPrompt
{
    private static readonly IReadOnlyList<string> Options =
    [
        "Allow once",
        "Allow for this project",
        "Allow always (global)",
        "Deny",
    ];

    public static async Task<ToolPermission?> ShowAsync(
        string name,
        string args,
        string risk,
        CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new Rule("[bold]Tool Confirmation[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[bold]Tool:[/] {Markup.Escape(name)}");

        string truncatedArgs = args.Length > 80 ? args[..77] + "..." : args;
        AnsiConsole.MarkupLine($"[bold]Args:[/] {Markup.Escape(truncatedArgs)}");

        if (string.Equals(risk, "high", StringComparison.OrdinalIgnoreCase))
            AnsiConsole.MarkupLine("[bold red]⚠ HIGH RISK[/]");
        else
            AnsiConsole.MarkupLine($"[grey]Risk: {Markup.Escape(risk)}[/]");

        int? choice = await InlinePrompt.ChooseAsync("Permission:", Options, cancellationToken).ConfigureAwait(false);

        return choice switch
        {
            0 => ToolPermission.Once,
            1 => ToolPermission.Project,
            2 => ToolPermission.Global,
            _ => null,
        };
    }
}
