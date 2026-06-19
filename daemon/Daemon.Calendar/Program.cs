// Fail fast if required config is missing — CalendarSyncService also validates this,
// but checking here exits before the DI container is built.
_ = Environment.GetEnvironmentVariable("DCAL_ICAL_URL")
    ?? throw new InvalidOperationException(
        "DCAL_ICAL_URL is required. Set it to the iCal subscription URL to sync from.");

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<Daemon.Calendar.CalendarDatabase>(_ => new Daemon.Calendar.CalendarDatabase("calendar.db"));
// Two-step so the endpoint handlers can inject CalendarSyncService to read LastSync.
builder.Services.AddSingleton<Daemon.Calendar.CalendarSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Daemon.Calendar.CalendarSyncService>());

var app = builder.Build();
app.Run();
