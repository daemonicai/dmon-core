using MailKit.Search;
using System.Threading.Channels;
using Dmail.Data;
using Dmail.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace Dmail.Services;

public sealed class ImapIdleWatcher : BackgroundService
{
    private readonly string _email;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly AccountService _accounts;
    private readonly OAuth2Service _oauth;
    private readonly ChannelWriter<Email> _channelWriter;
    private readonly int _backfillMonths;
    private readonly ILogger<ImapIdleWatcher> _logger;

    private DateTime _lastKnownDate = DateTime.UtcNow;

    public ImapIdleWatcher(
        string email,
        ISqliteConnectionFactory connectionFactory,
        AccountService accounts,
        OAuth2Service oauth,
        Channel<Email> channel,
        int backfillMonths,
        ILogger<ImapIdleWatcher> logger)
    {
        _email = email;
        _connectionFactory = connectionFactory;
        _accounts = accounts;
        _oauth = oauth;
        _channelWriter = channel.Writer;
        _backfillMonths = backfillMonths;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IMAP watcher starting for {Email}", _email);

        // Check if backfill is needed (task 5.8)
        var backfillState = await GetBackfillStateAsync();
        if (backfillState.status == "pending" || backfillState.status == "in_progress")
        {
            await RunBackfillAsync(backfillState.lastUid, stoppingToken);
        }

        // Main IDLE loop with exponential backoff (task 5.5)
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIdleLoopAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(1); // Reset on clean exit
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IDLE loop failed for {Email}, reconnecting in {Delay}s", _email, backoff.TotalSeconds);
                await Task.Delay(backoff, stoppingToken);
                backoff = Min(backoff * 2, TimeSpan.FromSeconds(60)); // Cap at 60s
            }
        }
    }

    private async Task RunIdleLoopAsync(CancellationToken ct)
    {
        var (accessToken, _, expiry) = await _accounts.GetTokensAsync(_email);

        // Task 5.2: Refresh token if needed
        if (accessToken == null || OAuth2Service.NeedsRefresh(expiry))
        {
            var (_, refreshToken, _) = await _accounts.GetTokensAsync(_email);
            if (refreshToken == null)
            {
                _logger.LogWarning("Cannot refresh — no refresh token for {Email}", _email);
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                return;
            }
            var (newToken, _) = await _oauth.RefreshTokenAsync(_email, refreshToken, ct);
            if (newToken == null) return;
            accessToken = newToken;
        }

        using var client = new ImapClient();

        // Task 5.3: Connect with OAuth2 SASL
        await client.ConnectAsync("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect, ct);
        var sasl = new SaslMechanismOAuth2(_email, accessToken);
        await client.AuthenticateAsync(sasl, ct);

        await client.Inbox!.OpenAsync(FolderAccess.ReadOnly, ct);
        _logger.LogInformation("Connected to IMAP for {Email}", _email);

        // Task 5.6: Gap-fill after reconnect
        await GapFillAsync(client, ct);

        // Track the highest message index already seen so the IDLE wake-up only
        // processes genuinely new arrivals.
        var inbox = client.Inbox!;
        var lastSeenCount = inbox.Count;

        // Task 5.4: IDLE listener
        while (!ct.IsCancellationRequested && client.IsConnected)
        {
            using var done = new CancellationTokenSource();
            using var registration = ct.Register(static state => ((CancellationTokenSource)state!).Cancel(), done);

            try
            {
                await client.IdleAsync(done.Token, ct);
            }
            catch (OperationCanceledException) { }

            if (inbox.Count > lastSeenCount)
            {
                _lastKnownDate = DateTime.UtcNow;

                // Fetch the new messages with their real IMAP UIDs (task B1).
                var summaries = await inbox.FetchAsync(
                    lastSeenCount, -1, MessageSummaryItems.UniqueId, ct);

                foreach (var summary in summaries)
                {
                    try
                    {
                        var message = await inbox.GetMessageAsync(summary.UniqueId, ct);
                        await EnqueueEmailAsync(summary.UniqueId, message, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch message {Uid} for {Email}", summary.UniqueId, _email);
                    }
                }

                lastSeenCount = inbox.Count;
            }
        }
    }

    /// <summary>
    /// Task 5.6: After reconnection, query for emails received during the gap.
    /// </summary>
    private async Task GapFillAsync(ImapClient client, CancellationToken ct)
    {
        try
        {
            var sinceDate = _lastKnownDate.AddMinutes(-1);
            var query = SearchQuery.DeliveredAfter(sinceDate);
            var uids = await client.Inbox!.SearchAsync(query, ct);

            if (uids.Count > 0)
            {
                _logger.LogInformation("Gap-fill: {Count} messages since {Since} for {Email}",
                    uids.Count, sinceDate, _email);

                foreach (var uid in uids)
                {
                    var message = await client.Inbox.GetMessageAsync(uid, ct);
                    await EnqueueEmailAsync(uid, message, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gap-fill failed for {Email}", _email);
        }
    }

    /// <summary>
    /// Tasks 5.7-5.8: Paced backfill of email history with resumable state.
    /// </summary>
    private async Task RunBackfillAsync(long? lastUid, CancellationToken ct)
    {
        _logger.LogInformation("Starting backfill for {Email}", _email);

        var (accessToken, _, expiry) = await _accounts.GetTokensAsync(_email);
        if (accessToken == null) return;

        if (OAuth2Service.NeedsRefresh(expiry))
        {
            var (_, refreshToken, _) = await _accounts.GetTokensAsync(_email);
            if (refreshToken != null)
            {
                var (newToken, _) = await _oauth.RefreshTokenAsync(_email, refreshToken, ct);
                if (newToken != null) accessToken = newToken;
            }
        }

        using var client = new ImapClient();
        await client.ConnectAsync("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect, ct);
        var sasl = new SaslMechanismOAuth2(_email, accessToken);
        await client.AuthenticateAsync(sasl, ct);
        await client.Inbox!.OpenAsync(FolderAccess.ReadOnly, ct);

        // Fetch UIDs from last N months
        var sinceDate = DateTime.UtcNow.AddMonths(-_backfillMonths);
        var uids = (await client.Inbox.SearchAsync(
            SearchQuery.DeliveredAfter(sinceDate), ct)).ToList();

        if (lastUid.HasValue)
        {
            uids = uids.Where(u => u.Id > (uint)lastUid.Value).ToList();
        }

        _logger.LogInformation("Backfill: {Count} messages to process for {Email}", uids.Count, _email);

        await UpdateBackfillStateAsync(lastUid ?? 0, "in_progress");

        // Process in batches of 50 with 5s delay (task 5.7)
        const int batchSize = 50;
        for (int i = 0; i < uids.Count; i += batchSize)
        {
            var batch = uids.Skip(i).Take(batchSize);
            foreach (var uid in batch)
            {
                try
                {
                    // GetMessageAsync(UniqueId) uses BODY.PEEK — does not mark as read.
                    var message = await client.Inbox.GetMessageAsync(uid, ct);
                    await EnqueueEmailAsync(uid, message, ct);
                    await UpdateBackfillStateAsync(uid.Id, "in_progress");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Backfill failed for UID {Uid}", uid);
                }
            }

            if (i + batchSize < uids.Count)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        var finalUid = uids.Count > 0 ? uids[^1].Id : (uint)lastUid.GetValueOrDefault();
        await UpdateBackfillStateAsync(finalUid, "complete");
        _logger.LogInformation("Backfill complete for {Email}", _email);
    }

    private async Task EnqueueEmailAsync(UniqueId uid, MimeKit.MimeMessage message, CancellationToken ct)
    {
        var email = new Email
        {
            Uid = uid.Id,
            Account = _email,
            Subject = message.Subject ?? "",
            Body = message.TextBody ?? message.HtmlBody ?? "",
            FromAddr = message.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            Date = message.Date.UtcDateTime,
            Labels = null,
            PendingEmbedding = true
        };

        await _channelWriter.WriteAsync(email, ct);
    }

    private async Task<(string status, long? lastUid)> GetBackfillStateAsync()
    {
        await using var connection = await _connectionFactory.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status, last_processed_uid FROM backfill_state WHERE account = @email";
        cmd.Parameters.AddWithValue("@email", _email);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var status = reader.GetString(0);
            var lastUid = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            return (status, lastUid);
        }
        return ("pending", null);
    }

    private async Task UpdateBackfillStateAsync(long lastUid, string status)
    {
        await using var connection = await _connectionFactory.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO backfill_state (account, last_processed_uid, status, updated_at)
            VALUES (@email, @uid, @status, datetime('now'))
            ON CONFLICT(account) DO UPDATE SET
                last_processed_uid = excluded.last_processed_uid,
                status = excluded.status,
                updated_at = excluded.updated_at";
        cmd.Parameters.AddWithValue("@email", _email);
        cmd.Parameters.AddWithValue("@uid", lastUid);
        cmd.Parameters.AddWithValue("@status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
