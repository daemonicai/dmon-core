using Dmail.Data;
using Dmail.Models;

namespace Dmail.Services;

public sealed class EmailRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public EmailRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task UpsertEmailAsync(Email email, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO data_emails (uid, account, subject, body, from_addr, date, labels, pending_embedding)
            VALUES (@uid, @account, @subject, @body, @from, @date, @labels, @pending)
            ON CONFLICT(account, uid) DO UPDATE SET
                subject = excluded.subject,
                body = excluded.body,
                from_addr = excluded.from_addr,
                date = excluded.date,
                labels = excluded.labels,
                pending_embedding = excluded.pending_embedding";
        cmd.Parameters.AddWithValue("@uid", (long)email.Uid);
        cmd.Parameters.AddWithValue("@account", email.Account);
        cmd.Parameters.AddWithValue("@subject", email.Subject);
        cmd.Parameters.AddWithValue("@body", email.Body);
        cmd.Parameters.AddWithValue("@from", email.FromAddr);
        cmd.Parameters.AddWithValue("@date", email.Date.ToString("O"));
        cmd.Parameters.AddWithValue("@labels", (object?)email.Labels ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pending", email.PendingEmbedding ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearPendingEmbeddingAsync(string account, uint uid, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE data_emails SET pending_embedding = 0 WHERE account = @account AND uid = @uid";
        cmd.Parameters.AddWithValue("@account", account);
        cmd.Parameters.AddWithValue("@uid", (long)uid);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkPendingEmbeddingAsync(string account, uint uid, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE data_emails SET pending_embedding = 1 WHERE account = @account AND uid = @uid";
        cmd.Parameters.AddWithValue("@account", account);
        cmd.Parameters.AddWithValue("@uid", (long)uid);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAccountIndexStatusAsync(string account, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE accounts
            SET indexed_email_count = (SELECT COUNT(*) FROM data_emails
                                       WHERE account = @account AND pending_embedding = 0),
                last_sync = @now
            WHERE email = @account";
        cmd.Parameters.AddWithValue("@account", account);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<Email>> GetPendingEmbeddingsAsync(int limit = 50, CancellationToken ct = default)
    {
        var emails = new List<Email>();
        await using var connection = await _connectionFactory.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT uid, account, subject, body, from_addr, date, labels
            FROM data_emails
            WHERE pending_embedding = 1
            ORDER BY date ASC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            emails.Add(new Email
            {
                Uid = (uint)reader.GetInt64(0),
                Account = reader.GetString(1),
                Subject = reader.GetString(2),
                Body = reader.GetString(3),
                FromAddr = reader.GetString(4),
                Date = DateTime.Parse(reader.GetString(5)),
                Labels = reader.IsDBNull(6) ? null : reader.GetString(6),
                PendingEmbedding = true
            });
        }
        return emails;
    }
}
