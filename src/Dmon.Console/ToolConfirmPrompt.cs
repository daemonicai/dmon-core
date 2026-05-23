using System.Text.Json;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Spectre.Console;

namespace Dmon.Console;

/// <summary>
/// Renders the tool confirmation prompt and returns the user's decision.
/// </summary>
public static class ToolConfirmPrompt
{
    public sealed record Result(bool Confirmed, bool Cancelled, bool ForProject, bool ForGlobal);

    public static Result Show(ToolConfirmRequestEvent evt)
    {
        string riskLabel = evt.Risk switch
        {
            RiskLevel.High => "[red]HIGH RISK[/]",
            RiskLevel.Low => "[yellow]LOW RISK[/]",
            _ => "No Risk"
        };

        string argsStr = FormatArgs(evt.Args);

        Color borderColor = evt.Risk == RiskLevel.High
            ? Color.Red
            : Color.Yellow;

        var panel = new Panel(
            $"[bold]{Markup.Escape(evt.Name)}[/]\n{riskLabel}\n\n[grey]{Markup.Escape(argsStr)}[/]")
        {
            Header = new PanelHeader("Tool Confirmation"),
            Border = evt.Risk == RiskLevel.High
                ? BoxBorder.Heavy
                : BoxBorder.Rounded,
            BorderStyle = new Style(borderColor)
        };

        AnsiConsole.Write(panel);

        // For high risk or composite bash: only Allow once / Deny
        if (evt.Risk == RiskLevel.High || IsCompositeBash(evt.Name, evt.Args))
        {
            string choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Action")
                    .AddChoices("Allow once", "Deny"));

            return choice == "Allow once"
                ? new Result(true, false, false, false)
                : new Result(false, true, false, false);
        }

        // Full options for low/none risk
        string fullChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Action")
                .AddChoices("Allow once", "Allow for project", "Allow globally", "Deny"));

        return fullChoice switch
        {
            "Allow once" => new Result(true, false, false, false),
            "Allow for project" => new Result(true, false, true, false),
            "Allow globally" => new Result(true, false, false, true),
            _ => new Result(false, true, false, false)
        };
    }

    private static string FormatArgs(object? args)
    {
        if (args is null) return "(none)";

        try
        {
            string json = JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
            if (json.Length > 500)
                json = json[..500] + "...";
            return json;
        }
        catch
        {
            return args.ToString() ?? "(unknown)";
        }
    }

    private static bool IsCompositeBash(string name, object? args)
    {
        if (!string.Equals(name, "bash", StringComparison.OrdinalIgnoreCase))
            return false;

        return true; // Conservative: always treat bash as composite for now
    }
}
