using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Dmon.Tools.Dcal;

internal sealed class CalendarClient : ICalendarStore
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string? _apiKey;

    /// <param name="baseUrl">Base URL of the calendar server, e.g. <c>http://localhost:5280</c>.</param>
    /// <param name="apiKey">Value sent in the <c>X-Api-Key</c> header. May be null if the server runs without auth.</param>
    /// <param name="httpClient">Optional client to reuse; one is created (and owned) when null.</param>
    public CalendarClient(string baseUrl, string? apiKey, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _apiKey = apiKey;
    }

    /// <inheritdoc/>
    public async Task<CalendarEvent?> FindNextAsync(string term, string? after, CancellationToken cancellationToken = default)
    {
        var url = new StringBuilder("api/events/next?term=").Append(Uri.EscapeDataString(term));
        if (!string.IsNullOrEmpty(after))
            url.Append("&after=").Append(Uri.EscapeDataString(after));

        using var req = new HttpRequestMessage(HttpMethod.Get, url.ToString());
        AddAuth(req);

        using HttpResponseMessage resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(resp, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CalendarEvent>(resp, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CalendarEvent>> ListUpcomingAsync(int maxResults, string? after, CancellationToken cancellationToken = default)
    {
        var url = new StringBuilder("api/events/upcoming?maxResults=").Append(maxResults);
        if (!string.IsNullOrEmpty(after))
            url.Append("&after=").Append(Uri.EscapeDataString(after));

        using var req = new HttpRequestMessage(HttpMethod.Get, url.ToString());
        AddAuth(req);

        using HttpResponseMessage resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CalendarEvent[]>(resp, cancellationToken).ConfigureAwait(false);
    }

    private void AddAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Add("X-Api-Key", _apiKey);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken cancellationToken)
    {
        if (resp.IsSuccessStatusCode)
            return;

        string detail = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string message = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Calendar server rejected the API key. Set DCAL_API_KEY to the value the server logged at startup.",
            _ => $"Calendar request failed ({(int)resp.StatusCode} {resp.ReasonPhrase}). {detail}".Trim(),
        };
        throw new CalendarApiException(message, resp.StatusCode);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken cancellationToken)
    {
        T? value = await resp.Content.ReadFromJsonAsync<T>(s_json, cancellationToken).ConfigureAwait(false);
        return value ?? throw new CalendarApiException("Calendar server returned an empty or unreadable response body.");
    }
}
