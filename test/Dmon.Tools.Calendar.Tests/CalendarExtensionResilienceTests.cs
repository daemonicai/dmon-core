using Microsoft.Extensions.AI;

namespace Dmon.Tools.Calendar.Tests;

public sealed class CalendarExtensionResilienceTests
{
    [Fact]
    public async Task UnreachableServer_LookupCalendar_ReturnsErrorString()
    {
        FakeHttpHandler handler = new(_ => throw new HttpRequestException("Connection refused"));
        CalendarExtension ext = new("http://localhost:19999", null, new HttpClient(handler));
        AIFunction tool = ext.Tools.Single(t => t.Name == "lookup_calendar");

        object? result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["term"] = "standup" }),
            CancellationToken.None);

        string text = result?.ToString() ?? string.Empty;
        Assert.False(string.IsNullOrEmpty(text));
        Assert.DoesNotContain("throw", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotFound_LookupCalendar_ReturnsNoEventFoundString()
    {
        FakeHttpHandler handler = new(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        CalendarExtension ext = new("http://localhost:19999", null, new HttpClient(handler));
        AIFunction tool = ext.Tools.Single(t => t.Name == "lookup_calendar");

        object? result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["term"] = "standup" }),
            CancellationToken.None);

        string text = result?.ToString() ?? string.Empty;
        Assert.Contains("No upcoming event found", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnreachableServer_ListUpcomingEvents_ReturnsErrorString()
    {
        FakeHttpHandler handler = new(_ => throw new HttpRequestException("Connection refused"));
        CalendarExtension ext = new("http://localhost:19999", null, new HttpClient(handler));
        AIFunction tool = ext.Tools.Single(t => t.Name == "list_upcoming_events");

        object? result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["maxResults"] = 5 }),
            CancellationToken.None);

        string text = result?.ToString() ?? string.Empty;
        Assert.False(string.IsNullOrEmpty(text));
        Assert.DoesNotContain("throw", text, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(respond(request));
        }
        catch (Exception ex)
        {
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }
}
