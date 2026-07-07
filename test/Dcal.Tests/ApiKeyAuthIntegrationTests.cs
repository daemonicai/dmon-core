using Microsoft.AspNetCore.Mvc.Testing;

namespace Dcal.Tests;

/// <summary>
/// Boot-level proof that the X-Api-Key middleware is unconditionally installed in the
/// REAL pipeline (Program.cs) — ApiKeyServiceTests only proves Validate's logic in
/// isolation, not that a request is actually rejected when the pipeline is wired up.
/// <see cref="EventsUpcoming_ApiKeyUnset_NoHeader_Returns401"/> is the regression guard
/// for Program.cs's removed `if (!string.IsNullOrEmpty(apiKey))` guard: it is the only
/// case here where DCAL_API_KEY is UNSET at boot (forcing ApiKeyService's auto-generate
/// branch), so it is the one case that would start passing under the OLD guarded code
/// if that `if` were ever reintroduced.
///
/// This is currently the only host-booting test class in Dcal.Tests. It sets process-
/// global env vars (DCAL_ICAL_URL/DCAL_API_KEY/DCAL_DATA_DIR) for the duration of each
/// test, so it shares the "DCAL env vars" xunit collection with CalendarSyncServiceTests
/// (which also mutates DCAL_ICAL_URL) to avoid racing across parallel test classes. Any
/// future host-booting class touching these env vars should join the same collection.
/// </summary>
[Collection("DCAL env vars")]
public sealed class ApiKeyAuthIntegrationTests : IDisposable
{
    private const string ApiKey = "integration-test-key";

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;

    public ApiKeyAuthIntegrationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"dcal-apikey-it-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dataDir);

        // Unreachable but well-formed URL: CalendarSyncService.SyncAsync catches
        // HttpRequestException, so the failed background sync does not crash the host —
        // LastSync just stays null, and /api/events/* respond 503 (not 401) once past auth.
        Environment.SetEnvironmentVariable("DCAL_ICAL_URL", "http://127.0.0.1:1/cal.ics");
        Environment.SetEnvironmentVariable("DCAL_API_KEY", ApiKey);
        Environment.SetEnvironmentVariable("DCAL_DATA_DIR", _dataDir);

        _factory = new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DCAL_ICAL_URL", null);
        Environment.SetEnvironmentVariable("DCAL_API_KEY", null);
        Environment.SetEnvironmentVariable("DCAL_DATA_DIR", null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task EventsUpcoming_ApiKeyUnset_NoHeader_Returns401()
    {
        // Unlike every other test in this class, DCAL_API_KEY is UNSET for this boot —
        // overriding the constructor's default so ApiKeyService takes the
        // auto-generate-and-persist branch (writing into _dataDir, per the constructor's
        // DCAL_DATA_DIR). The host build is deferred until CreateClient(), so this
        // override still takes effect even though the constructor ran first.
        Environment.SetEnvironmentVariable("DCAL_API_KEY", null);

        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/events/upcoming");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EventsUpcoming_ApiKeySet_NoHeader_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/events/upcoming");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EventsUpcoming_CorrectKey_ReachesHandler()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        var response = await client.GetAsync("/api/events/upcoming");

        // No sync has completed against the unreachable feed, so the handler
        // responds 503 — the point here is that auth let the request through at all.
        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EventsNext_WrongKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "not-the-right-key");

        var response = await client.GetAsync("/api/events/next?term=standup");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_NoKey_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
