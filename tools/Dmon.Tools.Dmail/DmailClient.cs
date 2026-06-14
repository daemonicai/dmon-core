using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daemonic.Dmail.Extension;

/// <summary>
/// Thin HTTP client over the Dmail agent API. Authenticates with the <c>X-Api-Key</c>
/// header and (de)serialises with web defaults to match Dmail's minimal-API JSON.
/// </summary>
internal sealed class DmailClient
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string? _apiKey;

    /// <param name="baseUrl">Base URL of the Dmail instance, e.g. <c>http://localhost:8080</c>.</param>
    /// <param name="apiKey">Value sent in the <c>X-Api-Key</c> header. May be null if Dmail runs without auth.</param>
    /// <param name="httpClient">Optional client to reuse; one is created (and owned) when null.</param>
    public DmailClient(string baseUrl, string? apiKey, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _apiKey = apiKey;
    }

    /// <summary>POST /api/search — hybrid keyword + semantic search.</summary>
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage resp = await PostAsync("api/search", request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<SearchResponse>(resp, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>POST /api/emails/list — recent/filtered messages, newest first.</summary>
    public async Task<EmailListResponse> ListAsync(EmailListRequest request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage resp = await PostAsync("api/emails/list", request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<EmailListResponse>(resp, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GET /api/emails/{uid} — full message; null when not found.</summary>
    public async Task<EmailDetail?> GetEmailAsync(int uid, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/emails/{uid}");
        AddAuth(req);

        using HttpResponseMessage resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(resp, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<EmailDetail>(resp, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T body, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: s_json),
        };
        AddAuth(req);

        HttpResponseMessage resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, cancellationToken).ConfigureAwait(false);
        return resp;
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
                "Dmail rejected the API key. Set DMAIL_API_KEY to the value Dmail logged at startup.",
            _ => $"Dmail request failed ({(int)resp.StatusCode} {resp.ReasonPhrase}). {detail}".Trim(),
        };
        throw new DmailApiException(message, resp.StatusCode);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken cancellationToken)
    {
        T? value = await resp.Content.ReadFromJsonAsync<T>(s_json, cancellationToken).ConfigureAwait(false);
        return value ?? throw new DmailApiException("Dmail returned an empty or unreadable response body.");
    }
}
