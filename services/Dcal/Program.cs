using Dcal;

// Fail fast if required config is missing — CalendarSyncService also validates this,
// but checking here exits before the DI container is built.
_ = Environment.GetEnvironmentVariable("DCAL_ICAL_URL")
    ?? throw new InvalidOperationException(
        "DCAL_ICAL_URL is required. Set it to the iCal subscription URL to sync from.");

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CalendarDatabase>(_ => new CalendarDatabase("calendar.db"));
// Two-step so the endpoint handlers can inject CalendarSyncService to read LastSync.
builder.Services.AddSingleton<CalendarSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CalendarSyncService>());

var app = builder.Build();

// 7.5 — X-Api-Key auth middleware (before endpoints), unconditionally installed:
// default-deny for everything except /health, regardless of whether DCAL_API_KEY
// was configured or auto-generated.
var apiKeyService = new ApiKeyService(builder.Configuration, app.Services.GetRequiredService<ILogger<ApiKeyService>>());
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next(context);
        return;
    }
    if (!context.Request.Headers.TryGetValue("X-Api-Key", out var key) || !apiKeyService.Validate(key))
    {
        context.Response.StatusCode = 401;
        return;
    }
    await next(context);
});

// 7.1 — GET /api/events/next
app.MapGet("/api/events/next", (CalendarSyncService sync, CalendarDatabase db, string? term, string? after) =>
{
    if (sync.LastSync is null)
        return Results.StatusCode(503);
    if (string.IsNullOrWhiteSpace(term))
        return Results.BadRequest("term is required");
    string effectiveAfter = after ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    CalendarRow? row = db.FindNext(term, effectiveAfter);
    return row is null ? Results.NotFound() : Results.Ok(ToDto(row));
});

// 7.2 — GET /api/events/upcoming
app.MapGet("/api/events/upcoming", (CalendarSyncService sync, CalendarDatabase db, int? maxResults, string? after) =>
{
    if (sync.LastSync is null)
        return Results.StatusCode(503);
    int clampedMax = Math.Clamp(maxResults ?? 5, 1, 50);
    string effectiveAfter = after ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    IReadOnlyList<CalendarRow> rows = db.ListUpcoming(clampedMax, effectiveAfter);
    return Results.Ok(rows.Select(ToDto).ToArray());
});

// 7.3 — POST /api/sync
app.MapPost("/api/sync", async (CalendarSyncService sync, CancellationToken ct) =>
{
    await sync.TriggerSyncAsync(ct);
    return Results.NoContent();
});

// 7.4 — GET /health
app.MapGet("/health", (CalendarSyncService sync, CalendarDatabase db) =>
    Results.Ok(new { lastSync = sync.LastSync, eventCount = db.Count() }));

app.Run();

static object ToDto(CalendarRow r) => new
{
    uid = r.Uid,
    title = r.Title,
    description = r.Description,
    location = r.Location,
    startUtc = r.StartUtc,
    endUtc = r.EndUtc
};
