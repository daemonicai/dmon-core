using Daemon.Calendar;

namespace Daemon.Calendar.Tests;

public sealed class CalendarDatabaseFindNextTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void FindNext_MatchingTerm_ReturnsCorrectRow()
    {
        CalendarDatabase db = new(_dbPath);
        CalendarRow expected = new("uid-1", "Team Meeting", "Weekly sync", "Room A", "2026-06-20T10:00:00Z", "2026-06-20T11:00:00Z");
        db.Upsert([expected]);

        CalendarRow? result = db.FindNext("meeting", "2000-01-01T00:00:00Z");

        Assert.NotNull(result);
        Assert.Equal(expected.Uid, result.Uid);
        Assert.Equal(expected.Title, result.Title);
        Assert.Equal(expected.Description, result.Description);
        Assert.Equal(expected.Location, result.Location);
        Assert.Equal(expected.StartUtc, result.StartUtc);
        Assert.Equal(expected.EndUtc, result.EndUtc);
    }

    [Fact]
    public void FindNext_NoMatchingTerm_ReturnsNull()
    {
        CalendarDatabase db = new(_dbPath);
        db.Upsert([new("uid-1", "Team Meeting", null, null, "2026-06-20T10:00:00Z", "2026-06-20T11:00:00Z")]);

        CalendarRow? result = db.FindNext("dentist", "2000-01-01T00:00:00Z");

        Assert.Null(result);
    }

    [Fact]
    public void FindNext_EventBeforeAfterFilter_ReturnsNull()
    {
        CalendarDatabase db = new(_dbPath);
        db.Upsert([new("uid-1", "Old Meeting", null, null, "2020-01-01T10:00:00Z", "2020-01-01T11:00:00Z")]);

        CalendarRow? result = db.FindNext("meeting", "2026-01-01T00:00:00Z");

        Assert.Null(result);
    }

    [Fact]
    public void FindNext_TimestampRoundTrip_ExactMatch()
    {
        const string timestamp = "2026-06-19T14:30:00Z";
        CalendarDatabase db = new(_dbPath);
        db.Upsert([new("uid-ts", "Timestamp Test", null, null, timestamp, "2026-06-19T15:00:00Z")]);

        CalendarRow? result = db.FindNext("timestamp", "2000-01-01T00:00:00Z");

        Assert.NotNull(result);
        Assert.Equal(timestamp, result.StartUtc);
    }

    [Fact]
    public void FindNext_AfterClearAndReupsert_StillWorks()
    {
        CalendarDatabase db = new(_dbPath);
        CalendarRow row = new("uid-r", "Resync Meeting", null, null, "2026-06-20T09:00:00Z", "2026-06-20T10:00:00Z");

        db.Upsert([row]);
        db.Clear();
        db.Upsert([row]);

        CalendarRow? result = db.FindNext("resync", "2000-01-01T00:00:00Z");

        Assert.NotNull(result);
        Assert.Equal("uid-r", result.Uid);
    }
}
