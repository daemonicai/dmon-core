using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using IcalCalendar = Ical.Net.Calendar;

namespace Dcal;

internal sealed class CalendarSyncService : BackgroundService
{
    private readonly CalendarDatabase _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CalendarSyncService> _logger;
    private readonly string _icalUrl;
    private readonly int _syncIntervalMinutes;
    private readonly int _recurrenceHorizonDays;

    public string? LastSync { get; private set; }

    public CalendarSyncService(
        CalendarDatabase db,
        IHttpClientFactory httpClientFactory,
        ILogger<CalendarSyncService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _icalUrl = Environment.GetEnvironmentVariable("DCAL_ICAL_URL")
            ?? throw new InvalidOperationException(
                "DCAL_ICAL_URL is required. Set it to the iCal subscription URL to sync from.");

        _syncIntervalMinutes = int.TryParse(
            Environment.GetEnvironmentVariable("DCAL_SYNC_INTERVAL_MINUTES"), out int interval)
            ? interval
            : 15;

        _recurrenceHorizonDays = int.TryParse(
            Environment.GetEnvironmentVariable("DCAL_RECURRENCE_HORIZON_DAYS"), out int horizon)
            ? horizon
            : 90;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SyncAsync(stoppingToken);

        using PeriodicTimer timer = new(TimeSpan.FromMinutes(_syncIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SyncAsync(stoppingToken);
    }

    public Task TriggerSyncAsync(CancellationToken cancellationToken = default)
        => SyncAsync(cancellationToken);

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = _httpClientFactory.CreateClient();
            string icalText = await client.GetStringAsync(_icalUrl, cancellationToken);

            IcalCalendar? calendar = IcalCalendar.Load(icalText);
            if (calendar is null)
            {
                _logger.LogWarning("iCal feed at {Url} parsed as empty", _icalUrl);
                return;
            }

            CalDateTime nowCal = CalDateTime.UtcNow;
            CalDateTime horizonCal = new(DateTime.UtcNow.AddDays(_recurrenceHorizonDays), CalDateTime.UtcTzId);

            List<CalendarRow> rows = [];

            foreach (CalendarEvent vevent in calendar.Events)
            {
                bool isRecurring = vevent.RecurrenceRules.Count > 0;

                IEnumerable<Occurrence> occurrences = vevent
                    .GetOccurrences(nowCal)
                    .TakeWhile(o => o.Period.StartTime < horizonCal);

                foreach (Occurrence occurrence in occurrences)
                {
                    DateTime startUtc = occurrence.Period.StartTime.AsUtc;
                    DateTime endUtc = (occurrence.Period.EffectiveEndTime ?? occurrence.Period.StartTime).AsUtc;

                    // Uid is nullable in Ical.Net (RFC 5545 permits omission). The GetHashCode
                    // fallback is non-deterministic across parses but harmless under the full-replace
                    // strategy (D4): the table is cleared each cycle so stale rows never accumulate.
                    string baseUid = vevent.Uid ?? vevent.GetHashCode().ToString();
                    string uid = isRecurring
                        ? $"{baseUid}_{startUtc:yyyyMMddTHHmmss}"
                        : baseUid;

                    rows.Add(new CalendarRow(
                        Uid: uid,
                        Title: vevent.Summary ?? string.Empty,
                        Description: vevent.Description,
                        Location: vevent.Location,
                        StartUtc: startUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        EndUtc: endUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")));
                }
            }

            _db.Clear();
            _db.Upsert(rows);
            LastSync = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            _logger.LogInformation(
                "Calendar sync complete: {Count} events through {Horizon:yyyy-MM-dd}",
                rows.Count,
                horizonCal.AsUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown — do not log as an error.
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to download iCal feed from {Url}", _icalUrl);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "iCal download timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Calendar sync failed");
        }
    }
}
