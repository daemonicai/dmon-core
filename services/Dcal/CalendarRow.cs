namespace Dcal;

internal record CalendarRow(
    string Uid,
    string Title,
    string? Description,
    string? Location,
    string StartUtc,
    string EndUtc);
