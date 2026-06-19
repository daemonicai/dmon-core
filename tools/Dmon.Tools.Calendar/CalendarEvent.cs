namespace Dmon.Tools.Calendar;

public record CalendarEvent(
    string Uid,
    string Title,
    string? Description,
    string? Location,
    string StartUtc,
    string EndUtc);
