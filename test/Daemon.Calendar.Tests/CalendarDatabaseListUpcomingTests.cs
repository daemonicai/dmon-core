using Daemon.Calendar;

namespace Daemon.Calendar.Tests;

public sealed class CalendarDatabaseListUpcomingTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void ListUpcoming_ReturnsChronologicalOrder()
    {
        CalendarDatabase db = new(_dbPath);
        db.Upsert(
        [
            new("uid-3", "Third", null, null, "2026-06-22T10:00:00Z", "2026-06-22T11:00:00Z"),
            new("uid-1", "First", null, null, "2026-06-20T10:00:00Z", "2026-06-20T11:00:00Z"),
            new("uid-2", "Second", null, null, "2026-06-21T10:00:00Z", "2026-06-21T11:00:00Z"),
        ]);

        IReadOnlyList<CalendarRow> results = db.ListUpcoming(10, "2000-01-01T00:00:00Z");

        Assert.Equal(3, results.Count);
        Assert.Equal("uid-1", results[0].Uid);
        Assert.Equal("uid-2", results[1].Uid);
        Assert.Equal("uid-3", results[2].Uid);
    }

    [Fact]
    public void ListUpcoming_MaxResults_LimitsReturnedRows()
    {
        CalendarDatabase db = new(_dbPath);
        db.Upsert(
        [
            new("uid-1", "E1", null, null, "2026-06-20T10:00:00Z", "2026-06-20T11:00:00Z"),
            new("uid-2", "E2", null, null, "2026-06-21T10:00:00Z", "2026-06-21T11:00:00Z"),
            new("uid-3", "E3", null, null, "2026-06-22T10:00:00Z", "2026-06-22T11:00:00Z"),
            new("uid-4", "E4", null, null, "2026-06-23T10:00:00Z", "2026-06-23T11:00:00Z"),
            new("uid-5", "E5", null, null, "2026-06-24T10:00:00Z", "2026-06-24T11:00:00Z"),
        ]);

        IReadOnlyList<CalendarRow> results = db.ListUpcoming(3, "2000-01-01T00:00:00Z");

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void ListUpcoming_EmptyStore_ReturnsEmptyList()
    {
        CalendarDatabase db = new(_dbPath);

        IReadOnlyList<CalendarRow> results = db.ListUpcoming(10, "2000-01-01T00:00:00Z");

        Assert.NotNull(results);
        Assert.Empty(results);
    }

    [Fact]
    public void ListUpcoming_AfterFilter_ExcludesEarlierEvents()
    {
        CalendarDatabase db = new(_dbPath);
        db.Upsert(
        [
            new("uid-old", "Old Event", null, null, "2020-01-01T10:00:00Z", "2020-01-01T11:00:00Z"),
            new("uid-new", "New Event", null, null, "2026-06-20T10:00:00Z", "2026-06-20T11:00:00Z"),
        ]);

        IReadOnlyList<CalendarRow> results = db.ListUpcoming(10, "2026-01-01T00:00:00Z");

        Assert.Single(results);
        Assert.Equal("uid-new", results[0].Uid);
    }
}
