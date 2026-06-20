using System.ComponentModel;
using System.Text;
using Dmon.Abstractions.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Dcal;

/// <summary>
/// dmon extension exposing calendar tools to an agent: <c>lookup_calendar</c> and
/// <c>list_upcoming_events</c>. Each tool calls the Dcal HTTP API.
/// </summary>
/// <remarks>
/// Wire it into a composition root with <c>builder.AddDcalAbilities()</c>
/// (configured from the <c>DCAL_BASE_URL</c> and <c>DCAL_API_KEY</c> environment
/// variables) or <c>builder.AddToolExtension(new DcalExtension(baseUrl, apiKey))</c>
/// for explicit configuration.
/// </remarks>
public sealed class DcalExtension : IToolExtension
{
    private const string DefaultBaseUrl = "http://localhost:5280";

    private readonly CalendarClient _client;
    private readonly AIFunction[] _tools;

    /// <summary>
    /// Creates the extension from environment configuration: <c>DCAL_BASE_URL</c>
    /// (default <c>http://localhost:5280</c>) and <c>DCAL_API_KEY</c>. Required by
    /// <c>AddToolExtension&lt;T&gt;()</c>, which needs a parameterless constructor.
    /// </summary>
    public DcalExtension()
        : this(
            Environment.GetEnvironmentVariable("DCAL_BASE_URL") is { Length: > 0 } url ? url : DefaultBaseUrl,
            Environment.GetEnvironmentVariable("DCAL_API_KEY"))
    {
    }

    /// <summary>Creates the extension against an explicit Dcal endpoint.</summary>
    /// <param name="baseUrl">Base URL of the calendar server, e.g. <c>http://localhost:5280</c>.</param>
    /// <param name="apiKey">Calendar API key (sent as <c>X-Api-Key</c>); null if the server runs without auth.</param>
    /// <param name="httpClient">Optional client to reuse for requests.</param>
    public DcalExtension(string baseUrl, string? apiKey, HttpClient? httpClient = null)
    {
        _client = new CalendarClient(baseUrl, apiKey, httpClient);
        _tools =
        [
            DmonAIFunctionFactory.Create(
                LookupCalendarAsync,
                "lookup_calendar",
                "Find the next upcoming calendar event matching a term. Returns the event's title, " +
                "time, location and description, or a message if none found."),
            DmonAIFunctionFactory.Create(
                ListUpcomingAsync,
                "list_upcoming_events",
                "List the next N upcoming calendar events, ordered by start time. Default 5, max 20."),
        ];
    }

    /// <inheritdoc />
    public string Name => "Calendar";

    /// <inheritdoc />
    public string Description =>
        "Look up calendar events and list upcoming events via the Dcal server.";

    /// <inheritdoc />
    public IEnumerable<AIFunction> Tools => _tools;

    /// <inheritdoc />
    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
        => PermissionResult.Allow;

    private async Task<string> LookupCalendarAsync(
        [Description("Search term to match against event title and description.")]
        string term,
        [Description("Optional. Only consider events starting at or after this ISO-8601 UTC timestamp, e.g. 2026-06-19T00:00:00Z. Defaults to the current time.")]
        string? after = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
            return "Provide a non-empty search term.";

        try
        {
            CalendarEvent? calendarEvent = await _client.FindNextAsync(term, after, cancellationToken).ConfigureAwait(false);
            return calendarEvent is null
                ? "No upcoming event found matching that term."
                : FormatEvent(calendarEvent);
        }
        catch (Exception ex) when (ex is CalendarApiException or HttpRequestException or TaskCanceledException)
        {
            return $"Could not look up calendar: {ex.Message}";
        }
    }

    private async Task<string> ListUpcomingAsync(
        [Description("Maximum number of events to return (1-20, default 5).")]
        int maxResults = 5,
        [Description("Optional. Only consider events starting at or after this ISO-8601 UTC timestamp, e.g. 2026-06-19T00:00:00Z. Defaults to the current time.")]
        string? after = null,
        CancellationToken cancellationToken = default)
    {
        maxResults = Math.Clamp(maxResults, 1, 20);

        try
        {
            IReadOnlyList<CalendarEvent> events = await _client.ListUpcomingAsync(maxResults, after, cancellationToken).ConfigureAwait(false);
            return events.Count == 0
                ? "No upcoming events found."
                : FormatEventList(events);
        }
        catch (Exception ex) when (ex is CalendarApiException or HttpRequestException or TaskCanceledException)
        {
            return $"Could not list events: {ex.Message}";
        }
    }

    private static string FormatEvent(CalendarEvent e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Title:    {e.Title}");
        sb.AppendLine($"Start:    {e.StartUtc}");
        sb.AppendLine($"End:      {e.EndUtc}");
        if (!string.IsNullOrWhiteSpace(e.Location))
            sb.AppendLine($"Location: {e.Location}");
        if (!string.IsNullOrWhiteSpace(e.Description))
            sb.AppendLine($"Notes:    {e.Description}");
        return sb.ToString().TrimEnd();
    }

    private static string FormatEventList(IReadOnlyList<CalendarEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{events.Count} upcoming event(s):");
        foreach (CalendarEvent e in events)
        {
            sb.AppendLine();
            sb.AppendLine($"  {e.StartUtc}  {e.Title}");
            if (!string.IsNullOrWhiteSpace(e.Location))
                sb.AppendLine($"    Location: {e.Location}");
        }
        return sb.ToString().TrimEnd();
    }
}
