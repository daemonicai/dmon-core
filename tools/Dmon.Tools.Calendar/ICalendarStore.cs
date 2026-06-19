namespace Dmon.Tools.Calendar;

public interface ICalendarStore
{
    Task<CalendarEvent?> FindNextAsync(string term, string? after, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CalendarEvent>> ListUpcomingAsync(int maxResults, string? after, CancellationToken cancellationToken = default);
}
