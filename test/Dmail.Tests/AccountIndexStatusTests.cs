using Dmail.Data;
using Dmail.Models;
using Dmail.Services;
using Microsoft.Data.Sqlite;

namespace Dmail.Tests;

public sealed class AccountIndexStatusTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dmail-acct-{Guid.NewGuid()}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();
        _factory = new SqliteConnectionFactory(connectionString);
        await new DatabaseInitializer(_factory).InitializeAsync();
    }

    public Task DisposeAsync()
    {
        try { SqliteConnection.ClearAllPools(); File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    private async Task InsertAccountAsync(string email)
    {
        await using var conn = await _factory.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO accounts (email, provider_type, account_state)
            VALUES (@email, 'gmail', 'active')";
        cmd.Parameters.AddWithValue("@email", email);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertEmailRowAsync(uint uid, string account, bool pending)
    {
        await using var conn = await _factory.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO data_emails (uid, account, subject, body, from_addr, date, pending_embedding)
            VALUES (@uid, @account, 'subj', 'body', 'f@x.com', '2024-01-01T00:00:00Z', @pending)
            ON CONFLICT(account, uid) DO NOTHING";
        cmd.Parameters.AddWithValue("@uid", (long)uid);
        cmd.Parameters.AddWithValue("@account", account);
        cmd.Parameters.AddWithValue("@pending", pending ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(long Count, string? LastSync)> ReadAccountStatusAsync(string email)
    {
        await using var conn = await _factory.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT indexed_email_count, last_sync FROM accounts WHERE email = @email";
        cmd.Parameters.AddWithValue("@email", email);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (0, null);
        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    [Fact]
    public async Task UpdateAccountIndexStatus_SetsCountToNonPendingEmails()
    {
        const string account = "test@example.com";
        await InsertAccountAsync(account);
        // 2 indexed (pending_embedding = 0), 1 still pending
        await InsertEmailRowAsync(1, account, pending: false);
        await InsertEmailRowAsync(2, account, pending: false);
        await InsertEmailRowAsync(3, account, pending: true);

        var repo = new EmailRepository(_factory);
        await repo.UpdateAccountIndexStatusAsync(account);

        var (count, lastSync) = await ReadAccountStatusAsync(account);
        Assert.Equal(2, count);
        Assert.NotNull(lastSync);
    }

    [Fact]
    public async Task UpdateAccountIndexStatus_LastSyncIsIso8601()
    {
        const string account = "test@example.com";
        await InsertAccountAsync(account);
        await InsertEmailRowAsync(1, account, pending: false);

        var before = DateTime.UtcNow.AddSeconds(-1);
        var repo = new EmailRepository(_factory);
        await repo.UpdateAccountIndexStatusAsync(account);

        var (_, lastSync) = await ReadAccountStatusAsync(account);
        Assert.NotNull(lastSync);
        var parsed = DateTime.Parse(lastSync!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(parsed >= before);
        Assert.True(parsed <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task UpdateAccountIndexStatus_AllPending_CountIsZero()
    {
        const string account = "test@example.com";
        await InsertAccountAsync(account);
        await InsertEmailRowAsync(1, account, pending: true);
        await InsertEmailRowAsync(2, account, pending: true);

        var repo = new EmailRepository(_factory);
        await repo.UpdateAccountIndexStatusAsync(account);

        var (count, _) = await ReadAccountStatusAsync(account);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task UpdateAccountIndexStatus_OnlyCountsEmailsForSpecifiedAccount()
    {
        const string account1 = "a@example.com";
        const string account2 = "b@example.com";
        await InsertAccountAsync(account1);
        await InsertAccountAsync(account2);
        await InsertEmailRowAsync(1, account1, pending: false);
        await InsertEmailRowAsync(2, account1, pending: false);
        await InsertEmailRowAsync(3, account2, pending: false);

        var repo = new EmailRepository(_factory);
        await repo.UpdateAccountIndexStatusAsync(account1);

        var (count1, _) = await ReadAccountStatusAsync(account1);
        var (count2, _) = await ReadAccountStatusAsync(account2);
        Assert.Equal(2, count1);
        Assert.Equal(0, count2); // account2 not updated
    }

    [Fact]
    public async Task UpdateAccountIndexStatus_CalledTwice_DoesNotDoubleCount()
    {
        const string account = "test@example.com";
        await InsertAccountAsync(account);
        await InsertEmailRowAsync(1, account, pending: false);
        await InsertEmailRowAsync(2, account, pending: false);

        var repo = new EmailRepository(_factory);
        await repo.UpdateAccountIndexStatusAsync(account);
        await repo.UpdateAccountIndexStatusAsync(account);

        var (count, _) = await ReadAccountStatusAsync(account);
        Assert.Equal(2, count);
    }
}
