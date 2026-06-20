using Dmail.Data;
using Dmail.Models;
using Dmail.Services;
using Microsoft.Data.Sqlite;

namespace Dmail;

public static class EndpointExtensions
{
    public static void MapHealthHandlers(this WebApplication app)
    {
        app.MapGet("/health", async (EmbeddingService embedding, ISqliteConnectionFactory connectionFactory, ImapWatcherManager watchers) =>
        {
            var modelOk = embedding.IsModelReady;
            var idleCount = watchers.ActiveWatcherCount;

            // Verify DB is accessible
            var dbOk = true;
            try
            {
                await using var db = await connectionFactory.OpenAsync();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync();
            }
            catch
            {
                dbOk = false;
            }

            var healthy = modelOk && dbOk;
            return healthy
                ? Results.Ok(new { status = "healthy", model_loaded = true, database_ok = true, idle_connections = idleCount })
                : Results.Json(new { status = "degraded", model_loaded = modelOk, database_ok = dbOk, idle_connections = idleCount }, statusCode: 503);
        });

        app.MapGet("/api/status", async (ISqliteConnectionFactory connectionFactory, ImapWatcherManager watchers) =>
        {
            await using var db = await connectionFactory.OpenAsync();
            var accounts = new List<object>();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                SELECT a.email, a.account_state, a.last_sync, a.indexed_email_count,
                       COALESCE(b.status, 'none') AS backfill_status
                FROM accounts a
                LEFT JOIN backfill_state b ON a.email = b.account";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var email = reader.GetString(0);
                var state = reader.GetString(1);
                var isIdle = watchers.ActiveWatcherCount > 0; // Simplified: watcher exists = idle active

                accounts.Add(new
                {
                    email,
                    state,
                    idle_active = isIdle,
                    last_sync = reader.IsDBNull(2) ? null : reader.GetString(2),
                    indexed_count = reader.GetInt64(3),
                    backfill_status = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }

            // Total emails across all accounts
            using var countCmd = db.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM data_emails";
            var totalEmails = (long)(await countCmd.ExecuteScalarAsync())!;

            return Results.Ok(new
            {
                accounts,
                total_emails = totalEmails,
                idle_connections = watchers.ActiveWatcherCount
            });
        });
    }

    // ---- Task 8.2: Search endpoint ----

    public static void MapSearchHandlers(this WebApplication app)
    {
        app.MapPost("/api/search", async (SearchRequest request, HybridSearchService search) =>
        {
            var response = await search.SearchAsync(request);
            return Results.Ok(response);
        }).RequireApiKey();
    }

    // ---- Task 8.3: Email listing endpoint ----

    public static void MapEmailHandlers(this WebApplication app)
    {
        app.MapPost("/api/emails/list", async (EmailListRequest request, ISqliteConnectionFactory connectionFactory, CancellationToken ct) =>
        {
            await using var db = await connectionFactory.OpenAsync(ct);
            var maxResults = Math.Min(request.MaxResults > 0 ? request.MaxResults : 20, 100);
            var offset = request.Page * maxResults;

            var clauses = new List<string>();
            var parameters = new List<SqliteParameter>();
            int idx = 0;

            if (!string.IsNullOrEmpty(request.Account))
            {
                clauses.Add($"account = @p{idx}");
                parameters.Add(new SqliteParameter($"@p{idx}", request.Account));
                idx++;
            }
            if (!string.IsNullOrEmpty(request.From))
            {
                clauses.Add($"from_addr = @p{idx}");
                parameters.Add(new SqliteParameter($"@p{idx}", request.From));
                idx++;
            }
            if (!string.IsNullOrEmpty(request.Since) && DateTime.TryParse(request.Since, out var since))
            {
                clauses.Add($"date >= @p{idx}");
                parameters.Add(new SqliteParameter($"@p{idx}", since.ToString("O")));
                idx++;
            }
            if (!string.IsNullOrEmpty(request.Labels))
            {
                clauses.Add($"labels LIKE @p{idx}");
                parameters.Add(new SqliteParameter($"@p{idx}", $"%{request.Labels}%"));
                idx++;
            }

            var where = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";

            // Count total
            using var countCmd = db.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM data_emails {where}";
            foreach (var p in parameters) countCmd.Parameters.Add(p);
            var totalCount = (long)(await countCmd.ExecuteScalarAsync(ct))!;

            // Fetch page
            using var fetchCmd = db.CreateCommand();
            fetchCmd.CommandText = $@"
                SELECT uid, account, subject, from_addr, date, labels, substr(body, 1, 100) AS preview
                FROM data_emails {where}
                ORDER BY date DESC
                LIMIT {maxResults} OFFSET {offset}";
            foreach (var p in parameters) fetchCmd.Parameters.Add(p);

            var results = new List<EmailListItem>();
            using var reader = await fetchCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new EmailListItem
                {
                    Uid = (uint)reader.GetInt64(0),
                    Subject = reader.GetString(2),
                    From = reader.GetString(3),
                    Date = reader.GetString(4),
                    Labels = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Preview = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }

            return Results.Ok(new EmailListResponse { Results = results.ToArray(), TotalCount = (int)totalCount });
        }).RequireApiKey();

        // ---- Task 8.4: Full email retrieval ----

        app.MapGet("/api/emails/{uid:int}", async (int uid, HttpContext ctx, ISqliteConnectionFactory connectionFactory, CancellationToken ct) =>
        {
            await using var db = await connectionFactory.OpenAsync(ct);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT uid, account, subject, body, from_addr, date, labels FROM data_emails WHERE uid = @uid";
            cmd.Parameters.AddWithValue("@uid", (long)uid);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Results.NotFound(new { error = "email not found" });

            return Results.Ok(new
            {
                uid = reader.GetInt64(0),
                account = reader.GetString(1),
                subject = reader.GetString(2),
                body = reader.GetString(3),
                from = reader.GetString(4),
                date = reader.GetString(5),
                labels = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }).RequireApiKey();
    }

    // ---- Task 8.5: List accounts ----

    public static void MapAccountHandlers(this WebApplication app)
    {
        app.MapGet("/api/accounts", async (ISqliteConnectionFactory connectionFactory) =>
        {
            await using var db = await connectionFactory.OpenAsync();
            var accounts = new List<object>();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT email, provider_type, account_state, last_sync, indexed_email_count FROM accounts";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                accounts.Add(new
                {
                    email = reader.GetString(0),
                    provider = reader.GetString(1),
                    state = reader.GetString(2),
                    last_sync = reader.IsDBNull(3) ? null : reader.GetString(3),
                    indexed_count = reader.GetInt64(4)
                });
            }
            return Results.Ok(accounts);
        });

        // ---- Task 8.6: Remove account ----

        app.MapDelete("/api/accounts/{email}", async (string email, ISqliteConnectionFactory connectionFactory, ImapWatcherManager watchers, CancellationToken ct) =>
        {
            await watchers.StopWatcherForAccountAsync(email);

            await using var db = await connectionFactory.OpenAsync(ct);
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM backfill_state WHERE account = @email;
                DELETE FROM data_emails WHERE account = @email;
                DELETE FROM accounts WHERE email = @email;
            ";
            cmd.Parameters.AddWithValue("@email", email);
            await cmd.ExecuteNonQueryAsync(ct);

            return Results.NoContent();
        });

        // Manual sync trigger (task 10.4)
        app.MapPost("/api/accounts/{email}/sync", async (string email, ImapWatcherManager watchers) =>
        {
            await watchers.StopWatcherForAccountAsync(email);
            await watchers.StartWatcherForAccountAsync(email);
            return Results.Accepted();
        });
    }

    public static void MapAuthHandlers(this WebApplication app)
    {
        app.MapGet("/api/auth/google/login", async (HttpContext ctx, OAuth2Service oauth, OAuth2StateStore store) =>
        {
            var redirectUri = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api/auth/google/callback";
            var (authUrl, codeVerifier, state) = oauth.BuildAuthorizationUrl(redirectUri);
            store.Store(state, codeVerifier);
            ctx.Response.Redirect(authUrl);
        });

        app.MapGet("/api/auth/google/callback", async (
            HttpContext ctx,
            string code,
            string state,
            OAuth2Service oauth,
            OAuth2StateStore store,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Dmail.AuthCallback");
            var codeVerifier = store.GetVerifier(state);
            if (codeVerifier == null)
            {
                return Results.BadRequest(new { error = "invalid_state" });
            }

            var redirectUri = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api/auth/google/callback";
            try
            {
                await oauth.ExchangeCodeAsync(code, codeVerifier, redirectUri);
                return Results.Redirect("/?auth=success");
            }
            catch (InvalidOperationException)
            {
                return Results.Redirect("/?auth=error");
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "Network error during OAuth2 callback");
                return Results.Redirect("/?auth=error");
            }
        });
    }
}
