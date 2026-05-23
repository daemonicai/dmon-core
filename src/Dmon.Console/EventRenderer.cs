using System.Text.Json;
using Dmon.Protocol.Events;
using Spectre.Console;

namespace Dmon.Console;

/// <summary>
/// Renders core-to-host events using Spectre.Console.
/// Handles streaming text display for <c>messageDelta</c> events,
/// tool execution status, errors, retries, and lifecycle events.
/// </summary>
public sealed class EventRenderer
{
    private bool _textBlockStarted;

    /// <summary>
    /// Renders a single event to the console.
    /// </summary>
    public void Render(Event evt)
    {
        switch (evt)
        {
            case AgentReadyEvent ready:
                AnsiConsole.MarkupLine(
                    $"[green]\u2713[/] Agent ready \u2014 protocol v{ready.ProtocolVersion}, core v{ready.CoreVersion}");
                break;

            case BootstrapNoticeEvent notice:
                RenderBootstrapNotice(notice);
                break;

            case TurnStartEvent:
                _textBlockStarted = false;
                AnsiConsole.WriteLine();
                break;

            case MessageStartEvent:
                // Role context is inferred from the stream deltas
                break;

            case MessageDeltaEvent msgDelta:
                RenderDelta(msgDelta);
                break;

            case MessageEndEvent:
                AnsiConsole.WriteLine();
                _textBlockStarted = false;
                break;

            case ToolExecutionStartEvent toolStart:
                RenderToolStart(toolStart);
                break;

            case ToolExecutionEndEvent toolEnd:
                RenderToolEnd(toolEnd);
                break;

            case TurnEndEvent:
                AnsiConsole.WriteLine();
                break;

            case ErrorEvent error:
                AnsiConsole.MarkupLine(
                    $"[red]\u2717 Error:[/] {Markup.Escape(error.Message)} [grey]({error.Code})[/]");
                break;

            case RetryAttemptEvent retry:
                AnsiConsole.MarkupLine(
                    $"[grey]\u21bb Retry {retry.Attempt}/{retry.MaxAttempts} \u2014 {retry.Reason} (next in {retry.NextDelayMs}ms)[/]");
                break;

            case ProviderSwitchedEvent switched:
                string timing = switched.EffectiveNextTurn ? " [grey](effective next turn)[/]" : "";
                AnsiConsole.MarkupLine(
                    $"[blue]\u21bb[/] Switched to [bold]{Markup.Escape(switched.Name)}[/] / {Markup.Escape(switched.Model)}{timing}");
                break;

            case ExtensionLoadedEvent loaded:
                AnsiConsole.MarkupLine(
                    $"[green]\u2713[/] Extension loaded: [bold]{Markup.Escape(loaded.Name)}[/] ({loaded.Tools.Count} tools)");
                break;

            case ExtensionUnloadedEvent unloaded:
                AnsiConsole.MarkupLine(
                    $"[grey]\u2717[/] Extension unloaded: {Markup.Escape(unloaded.Name)}");
                break;

            case ExtensionErrorEvent extError:
                AnsiConsole.MarkupLine(
                    $"[red]\u2717[/] Extension error: {Markup.Escape(extError.Source)} ({Markup.Escape(extError.Phase)})");
                break;

            case CompactionStartEvent compact:
                AnsiConsole.MarkupLine($"[grey]Compacting: {Markup.Escape(compact.Reason)}[/]");
                break;

            case CompactionEndEvent compactEnd:
                AnsiConsole.MarkupLine($"[grey]Compaction done: {Markup.Escape(compactEnd.Result)}[/]");
                break;

            case CapabilityIgnoredEvent capIgnore:
                AnsiConsole.MarkupLine(
                    $"[grey]Capability ignored: {capIgnore.Capability}={Markup.Escape(capIgnore.RequestedValue)} \u2014 {Markup.Escape(capIgnore.Reason)}[/]");
                break;

            case SessionUpdatedEvent sessionUpd:
                AnsiConsole.MarkupLine(
                    $"[blue]Session:[/] {Markup.Escape(sessionUpd.Title)} [grey]({sessionUpd.SessionId})[/]");
                break;

            case AuthLoginCompleteEvent loginOk:
                AnsiConsole.MarkupLine($"[green]\u2713[/] Logged in to {Markup.Escape(loginOk.Provider)}");
                break;

            case AuthLogoutCompleteEvent logoutOk:
                AnsiConsole.MarkupLine($"[grey]\u2713[/] Logged out of {Markup.Escape(logoutOk.Provider)}");
                break;

            case AuthLoginFailedEvent loginFail:
                AnsiConsole.MarkupLine(
                    $"[red]\u2717[/] Login failed for {Markup.Escape(loginFail.Provider)}: {Markup.Escape(loginFail.Reason)}");
                break;

            // Quiet events — not rendered directly
            case AgentStartEvent:
            case AgentEndEvent:
            case ResponseEvent:
            case AuthStatusResultEvent:
            case ModelListResultEvent:
                break;

            default:
                // Unknown event types — ignore gracefully
                break;
        }
    }

    private void RenderDelta(MessageDeltaEvent msgDelta)
    {
        if (msgDelta.Delta is not JsonElement deltaElement)
            return;

        if (!deltaElement.TryGetProperty("type", out JsonElement typeProp))
            return;

        string? deltaType = typeProp.GetString();

        switch (deltaType)
        {
            case "start":
                _textBlockStarted = true;
                break;

            case "textStart":
                if (!_textBlockStarted)
                    AnsiConsole.WriteLine();
                _textBlockStarted = true;
                break;

            case "textDelta":
                if (deltaElement.TryGetProperty("delta", out JsonElement textProp))
                {
                    string? text = textProp.GetString();
                    if (text is not null)
                        AnsiConsole.Write(new Markup(Markup.Escape(text)));
                }
                break;

            case "textEnd":
                // Already streamed via textDelta; full content available for completeness
                break;

            case "thinkingStart":
                AnsiConsole.Write(new Markup("[grey]"));
                break;

            case "thinkingDelta":
                if (deltaElement.TryGetProperty("delta", out JsonElement thinkProp))
                {
                    string? thinkText = thinkProp.GetString();
                    if (thinkText is not null)
                        AnsiConsole.Write(new Markup($"[grey]{Markup.Escape(thinkText)}[/]"));
                }
                break;

            case "thinkingEnd":
                AnsiConsole.Write(new Markup("[/]"));
                AnsiConsole.WriteLine();
                break;

            case "toolCallStart":
                AnsiConsole.Markup("[grey]\U0001f527 [/]");
                break;

            case "toolCallDelta":
                if (deltaElement.TryGetProperty("delta", out JsonElement tcProp))
                {
                    string? tc = tcProp.GetString();
                    if (tc is not null)
                        AnsiConsole.Write(new Markup($"[grey]{Markup.Escape(tc)}[/]"));
                }
                break;

            case "toolCallEnd":
                AnsiConsole.WriteLine();
                break;

            case "done":
                if (deltaElement.TryGetProperty("reason", out JsonElement reasonProp))
                {
                    string? reason = reasonProp.GetString();
                    AnsiConsole.MarkupLine($"[grey]({reason})[/]");
                }
                break;

            case "error":
                if (deltaElement.TryGetProperty("reason", out JsonElement errProp))
                {
                    string? reason = errProp.GetString();
                    AnsiConsole.MarkupLine(
                        $"[red]Stream error: {Markup.Escape(reason ?? "unknown")}[/]");
                }
                break;
        }
    }

    private static void RenderBootstrapNotice(BootstrapNoticeEvent notice)
    {
        string createdList = string.Join(
            "\n", notice.Created.Select(f => $"  \u2022 {f}"));

        var panel = new Panel(
            $"[blue]{Markup.Escape(notice.Path)}[/]\n{createdList}")
        {
            Header = new PanelHeader("[blue]Bootstrap[/]"),
            Border = BoxBorder.Rounded
        };
        AnsiConsole.Write(panel);
    }

    private static void RenderToolStart(ToolExecutionStartEvent toolStart)
    {
        string name = Markup.Escape(toolStart.Name);
        AnsiConsole.MarkupLine(
            $"[grey]\u2699 Running tool:[/] [yellow]{name}[/]");

        if (toolStart.Args is not null)
        {
            try
            {
                string argsJson = JsonSerializer.Serialize(
                    toolStart.Args, new JsonSerializerOptions { WriteIndented = true });
                if (argsJson.Length > 300)
                    argsJson = argsJson[..300] + "...";
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(argsJson)}[/]");
            }
            catch
            {
                string str = toolStart.Args.ToString() ?? "";
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(str)}[/]");
            }
        }
    }

    private static void RenderToolEnd(ToolExecutionEndEvent toolEnd)
    {
        string icon = toolEnd.IsError
            ? "[red]\u2717[/]"
            : "[green]\u2713[/]";
        AnsiConsole.MarkupLine($"{icon} Tool completed");

        if (toolEnd.IsError && toolEnd.Result is not null)
        {
            try
            {
                string errJson = JsonSerializer.Serialize(
                    toolEnd.Result, new JsonSerializerOptions { WriteIndented = true });
                if (errJson.Length > 300)
                    errJson = errJson[..300] + "...";
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(errJson)}[/]");
            }
            catch
            {
                string str = toolEnd.Result.ToString() ?? "";
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(str)}[/]");
            }
        }
    }
}
