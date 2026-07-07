using System.Net;
using System.Text;
using Dcal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Dcal.Tests;

// Shares the "DCAL env vars" collection with ApiKeyAuthIntegrationTests: both mutate the
// process-global DCAL_ICAL_URL env var, and xunit runs distinct test classes in parallel
// by default, so without this they can race each other's Set/Dispose-time null.
[Collection("DCAL env vars")]
public sealed class CalendarSyncServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");

    public CalendarSyncServiceTests()
    {
        Environment.SetEnvironmentVariable("DCAL_ICAL_URL", "http://fake/calendar.ics");
        Environment.SetEnvironmentVariable("DCAL_RECURRENCE_HORIZON_DAYS", "365");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DCAL_ICAL_URL", null);
        Environment.SetEnvironmentVariable("DCAL_RECURRENCE_HORIZON_DAYS", null);

        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public async Task TriggerSync_PopulatesDatabase()
    {
        const string ical = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:event-1
            SUMMARY:Team Meeting
            DTSTART:20260620T100000Z
            DTEND:20260620T110000Z
            END:VEVENT
            END:VCALENDAR
            """;

        CalendarDatabase db = new(_dbPath);
        CalendarSyncService svc = CreateService(db, ical);

        await svc.TriggerSyncAsync(CancellationToken.None);

        Assert.Equal(1, db.Count());
        CalendarRow? row = db.FindNext("meeting", "2000-01-01T00:00:00Z");
        Assert.NotNull(row);
        Assert.Equal("Team Meeting", row.Title);
    }

    [Fact]
    public async Task TriggerSync_ResyncRemovesDeletedEvent()
    {
        const string icalBoth = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:event-a
            SUMMARY:Event A
            DTSTART:20260620T100000Z
            DTEND:20260620T110000Z
            END:VEVENT
            BEGIN:VEVENT
            UID:event-b
            SUMMARY:Event B
            DTSTART:20260621T100000Z
            DTEND:20260621T110000Z
            END:VEVENT
            END:VCALENDAR
            """;

        const string icalOnlyA = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:event-a
            SUMMARY:Event A
            DTSTART:20260620T100000Z
            DTEND:20260620T110000Z
            END:VEVENT
            END:VCALENDAR
            """;

        CalendarDatabase db = new(_dbPath);

        CalendarSyncService svcFirst = CreateService(db, icalBoth);
        await svcFirst.TriggerSyncAsync(CancellationToken.None);
        Assert.Equal(2, db.Count());

        CalendarSyncService svcSecond = CreateService(db, icalOnlyA);
        await svcSecond.TriggerSyncAsync(CancellationToken.None);
        Assert.Equal(1, db.Count());

        CalendarRow? remaining = db.FindNext("Event A", "2000-01-01T00:00:00Z");
        Assert.NotNull(remaining);
    }

    [Fact]
    public async Task TriggerSync_RecurringEvent_ExpandsOccurrences()
    {
        const string ical = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:weekly-1
            SUMMARY:Weekly Standup
            DTSTART:20260620T090000Z
            DTEND:20260620T093000Z
            RRULE:FREQ=WEEKLY;COUNT=5
            END:VEVENT
            END:VCALENDAR
            """;

        CalendarDatabase db = new(_dbPath);
        CalendarSyncService svc = CreateService(db, ical);

        await svc.TriggerSyncAsync(CancellationToken.None);

        Assert.Equal(5, db.Count());
    }

    [Fact]
    public async Task TriggerSync_HorizonBoundary_LimitsOccurrences()
    {
        Environment.SetEnvironmentVariable("DCAL_RECURRENCE_HORIZON_DAYS", "2");

        FakeTimeProvider clock = new(FixedNow);
        string today = clock.GetUtcNow().ToString("yyyyMMdd");
        string ical = $"""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:daily-1
            SUMMARY:Daily Standup
            DTSTART:{today}T090000Z
            DTEND:{today}T093000Z
            RRULE:FREQ=DAILY;COUNT=30
            END:VEVENT
            END:VCALENDAR
            """;

        CalendarDatabase db = new(_dbPath);
        CalendarSyncService svc = CreateService(db, ical, clock);

        await svc.TriggerSyncAsync(CancellationToken.None);

        int count = db.Count();
        Assert.True(count > 0, "Expected at least one occurrence within horizon.");
        Assert.True(count <= 3, $"Expected at most 3 occurrences for 2-day horizon, got {count}.");
    }

    // A fixed clock at the start of the day the fixtures use, so the hard-coded
    // event dates are always at-or-after "now" and the occurrence window is stable
    // regardless of wall-clock time.
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);

    private static CalendarSyncService CreateService(
        CalendarDatabase db, string icalContent, TimeProvider? timeProvider = null)
    {
        FakeHttpClientFactory factory = new(icalContent);
        return new CalendarSyncService(
            db,
            factory,
            NullLogger<CalendarSyncService>.Instance,
            timeProvider ?? new FakeTimeProvider(FixedNow));
    }
}

internal sealed class FakeHttpClientFactory(string icalContent) : IHttpClientFactory
{
    public HttpClient CreateClient(string name = "")
        => new(new FakeIcalHandler(icalContent));
}

internal sealed class FakeIcalHandler(string icalContent) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(icalContent, Encoding.UTF8, "text/calendar")
        });
}
