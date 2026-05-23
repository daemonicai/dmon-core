using Daemon.Protocol.Enums;
using Daemon.Protocol.Events;
using Spectre.Console;

namespace Daemon.Console;

/// <summary>
/// Renders the UI input request prompt and returns the user's response.
/// Supports Text, Secret (masked), and Select kinds.
/// </summary>
public static class UiInputPrompt
{
    public sealed record Result(string? Value, bool Cancelled);

    public static Result Show(UiInputRequestEvent evt)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(evt.Prompt)}[/]");

        return evt.Kind switch
        {
            UiInputKind.Secret => ShowSecret(),
            UiInputKind.Select => ShowSelect(evt.Options),
            _ => ShowText()
        };
    }

    private static Result ShowText()
    {
        try
        {
            string value = AnsiConsole.Prompt(
                new TextPrompt<string>(string.Empty)
                    .AllowEmpty());
            return new Result(string.IsNullOrWhiteSpace(value) ? null : value, false);
        }
        catch
        {
            return new Result(null, true);
        }
    }

    private static Result ShowSecret()
    {
        try
        {
            string value = AnsiConsole.Prompt(
                new TextPrompt<string>(string.Empty)
                    .Secret());
            return new Result(string.IsNullOrWhiteSpace(value) ? null : value, false);
        }
        catch
        {
            return new Result(null, true);
        }
    }

    private static Result ShowSelect(IReadOnlyList<string>? options)
    {
        if (options is null || options.Count == 0)
            return new Result(null, false);

        try
        {
            string choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .AddChoices(options));
            return new Result(choice, false);
        }
        catch
        {
            return new Result(null, true);
        }
    }
}
