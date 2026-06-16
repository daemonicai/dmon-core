using System.ComponentModel;
using System.Text;
using Dmon.Abstractions.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Dmail;

/// <summary>
/// dmon extension exposing Dmail email tools to an agent: <c>search_email</c>,
/// <c>check_new_messages</c> and <c>get_email</c>. Each tool calls the Dmail HTTP API.
/// </summary>
/// <remarks>
/// Wire it into a composition root with <c>builder.AddToolExtension&lt;DmailExtension&gt;()</c>
/// (configured from the <c>DMAIL_BASE_URL</c> and <c>DMAIL_API_KEY</c> environment
/// variables) or <c>builder.AddToolExtension(new DmailExtension(baseUrl, apiKey))</c> for
/// explicit configuration.
/// </remarks>
public sealed class DmailExtension : IToolExtension
{
    private const string DefaultBaseUrl = "http://localhost:8080";

    private readonly DmailClient _client;
    private readonly AIFunction[] _tools;

    /// <summary>
    /// Creates the extension from environment configuration: <c>DMAIL_BASE_URL</c>
    /// (default <c>http://localhost:8080</c>) and <c>DMAIL_API_KEY</c>. Required by
    /// <c>AddToolExtension&lt;T&gt;()</c>, which needs a parameterless constructor.
    /// </summary>
    public DmailExtension()
        : this(
            Environment.GetEnvironmentVariable("DMAIL_BASE_URL") is { Length: > 0 } url ? url : DefaultBaseUrl,
            Environment.GetEnvironmentVariable("DMAIL_API_KEY"))
    {
    }

    /// <summary>Creates the extension against an explicit Dmail endpoint.</summary>
    /// <param name="baseUrl">Base URL of the Dmail instance, e.g. <c>http://localhost:8080</c>.</param>
    /// <param name="apiKey">Dmail API key (sent as <c>X-Api-Key</c>); null if Dmail runs without auth.</param>
    /// <param name="httpClient">Optional client to reuse for requests.</param>
    public DmailExtension(string baseUrl, string? apiKey, HttpClient? httpClient = null)
    {
        _client = new DmailClient(baseUrl, apiKey, httpClient);
        _tools =
        [
            DmonAIFunctionFactory.Create(
                SearchEmailAsync,
                "search_email",
                "Search the user's email by query. Runs a hybrid keyword + semantic search over " +
                "subject, sender and body and returns a ranked shortlist of matches, each with a " +
                "uid, date, sender, subject and snippet. Use this to find messages about a topic. " +
                "Follow up with get_email and a uid to read a full message."),
            DmonAIFunctionFactory.Create(
                CheckNewMessagesAsync,
                "check_new_messages",
                "Check for new/recent email. Returns how many messages have arrived since a given " +
                "time (defaulting to the last 24 hours) plus a newest-first list with uid, date, " +
                "sender, subject and preview. Use this to see if there is new mail."),
            DmonAIFunctionFactory.Create(
                GetEmailAsync,
                "get_email",
                "Retrieve the full content of a single email by its uid (as returned by " +
                "search_email or check_new_messages), including the complete body text."),
        ];
    }

    /// <inheritdoc />
    public string Name => "Dmail";

    /// <inheritdoc />
    public string Description =>
        "Search the user's email and check for new messages via the Dmail API.";

    /// <inheritdoc />
    public IEnumerable<AIFunction> Tools => _tools;

    /// <summary>
    /// Allows the metadata-only tools without prompting; full-message retrieval
    /// (<c>get_email</c>) still prompts because it exposes complete private content.
    /// </summary>
    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
        => call.Name == "get_email" ? PermissionResult.Prompt : PermissionResult.Allow;

    private async Task<string> SearchEmailAsync(
        [Description("What to search for — a natural-language query and/or keywords. Matched against subject, sender and body using hybrid keyword + semantic search.")]
        string query,
        [Description("Optional. Only include messages from this sender address (substring match).")]
        string? from = null,
        [Description("Optional. Only include messages on/after this ISO-8601 UTC timestamp, e.g. 2026-06-01T00:00:00Z.")]
        string? since = null,
        [Description("Maximum number of results to return (1-10, default 5).")]
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Provide a non-empty query to search for.";

        var request = new SearchRequest
        {
            Semantic = query,
            Keywords = Tokenize(query),
            From = string.IsNullOrWhiteSpace(from) ? null : from,
            Since = string.IsNullOrWhiteSpace(since) ? null : since,
            MaxResults = Math.Clamp(maxResults, 1, 10),
        };

        try
        {
            SearchResponse resp = await _client.SearchAsync(request, cancellationToken).ConfigureAwait(false);
            return FormatSearch(resp);
        }
        catch (Exception ex) when (ex is DmailApiException or HttpRequestException or TaskCanceledException)
        {
            return $"Could not search email: {ex.Message}";
        }
    }

    private async Task<string> CheckNewMessagesAsync(
        [Description("Optional. Only count/list messages received on/after this ISO-8601 UTC timestamp. Defaults to the last 24 hours.")]
        string? since = null,
        [Description("Maximum number of recent messages to list (1-50, default 10).")]
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        string effectiveSince = string.IsNullOrWhiteSpace(since)
            ? DateTimeOffset.UtcNow.AddHours(-24).ToString("yyyy-MM-ddTHH:mm:ssZ")
            : since;

        var request = new EmailListRequest
        {
            Since = effectiveSince,
            MaxResults = Math.Clamp(maxResults, 1, 50),
            Page = 0,
        };

        try
        {
            EmailListResponse resp = await _client.ListAsync(request, cancellationToken).ConfigureAwait(false);
            return FormatNewMessages(resp, effectiveSince);
        }
        catch (Exception ex) when (ex is DmailApiException or HttpRequestException or TaskCanceledException)
        {
            return $"Could not check for new messages: {ex.Message}";
        }
    }

    private async Task<string> GetEmailAsync(
        [Description("The Dmail message uid, as returned by search_email or check_new_messages.")]
        int uid,
        CancellationToken cancellationToken = default)
    {
        try
        {
            EmailDetail? email = await _client.GetEmailAsync(uid, cancellationToken).ConfigureAwait(false);
            return email is null
                ? $"No email found with uid {uid}."
                : FormatEmail(email);
        }
        catch (Exception ex) when (ex is DmailApiException or HttpRequestException or TaskCanceledException)
        {
            return $"Could not retrieve email {uid}: {ex.Message}";
        }
    }

    private static string[] Tokenize(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToArray();

    private static string FormatSearch(SearchResponse resp)
    {
        if (resp.Results.Length == 0)
            return "No matching emails found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {resp.TotalFound} matching email(s); showing top {resp.Results.Length}:");
        foreach (SearchResult r in resp.Results)
        {
            sb.AppendLine();
            sb.AppendLine($"[uid {r.Uid}] {r.Date} — {r.From}");
            sb.AppendLine($"  Subject: {Truncate(r.Subject, 120)}");
            if (!string.IsNullOrWhiteSpace(r.Snippet))
                sb.AppendLine($"  {Truncate(r.Snippet, 200)}");
        }
        sb.Append("Use get_email with a uid to read a full message.");
        return sb.ToString();
    }

    private static string FormatNewMessages(EmailListResponse resp, string since)
    {
        if (resp.Results.Length == 0)
            return $"No new messages since {since}.";

        var sb = new StringBuilder();
        sb.AppendLine($"{resp.TotalCount} message(s) since {since}; showing most recent {resp.Results.Length}:");
        foreach (EmailListItem m in resp.Results)
        {
            sb.AppendLine();
            sb.AppendLine($"[uid {m.Uid}] {m.Date} — {m.From}");
            sb.AppendLine($"  Subject: {Truncate(m.Subject, 120)}");
            if (!string.IsNullOrWhiteSpace(m.Preview))
                sb.AppendLine($"  {Truncate(m.Preview, 160)}");
        }
        sb.Append("Use get_email with a uid to read a full message.");
        return sb.ToString();
    }

    private static string FormatEmail(EmailDetail e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"uid:     {e.Uid}");
        sb.AppendLine($"account: {e.Account}");
        sb.AppendLine($"from:    {e.From}");
        sb.AppendLine($"date:    {e.Date}");
        if (!string.IsNullOrWhiteSpace(e.Labels))
            sb.AppendLine($"labels:  {e.Labels}");
        sb.AppendLine($"subject: {e.Subject}");
        sb.AppendLine();
        sb.Append(e.Body);
        return sb.ToString();
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string flattened = text.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= max ? flattened : string.Concat(flattened.AsSpan(0, max), "…");
    }
}
