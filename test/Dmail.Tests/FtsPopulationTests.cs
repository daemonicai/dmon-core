using Daemonic.Dmail.Data;
using Daemonic.Dmail.Models;
using Daemonic.Dmail.Services;
using Microsoft.Data.Sqlite;

namespace Daemonic.Dmail.Tests;

/// <summary>
/// B2: external-content FTS5 (content='data_emails') is not auto-maintained by SQLite.
/// These tests assert the sync triggers keep emails_fts populated on insert/update/delete
/// so BM25 MATCH queries resolve to the right rows.
/// </summary>
public sealed class FtsPopulationTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dmail-fts-{Guid.NewGuid()}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false // avoid pooled handles keeping the temp file open at teardown
        }.ToString();
        _factory = new SqliteConnectionFactory(connectionString);
        await new DatabaseInitializer(_factory).InitializeAsync();
    }

    public Task DisposeAsync()
    {
        try { SqliteConnection.ClearAllPools(); File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    private static Email MakeEmail(uint uid, string subject, string body) => new()
    {
        Uid = uid,
        Account = "user@example.com",
        Subject = subject,
        Body = body,
        FromAddr = "sender@example.com",
        Date = DateTime.UtcNow,
        PendingEmbedding = false
    };

    private async Task<List<uint>> MatchAsync(string ftsQuery)
    {
        await using var conn = await _factory.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT e.uid
            FROM emails_fts
            JOIN data_emails e ON emails_fts.rowid = e.rowid
            WHERE emails_fts MATCH @q
            ORDER BY bm25(emails_fts)";
        cmd.Parameters.AddWithValue("@q", ftsQuery);

        var uids = new List<uint>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            uids.Add((uint)reader.GetInt64(0));
        return uids;
    }

    [Fact]
    public async Task Insert_PopulatesFts_FindableByMatch()
    {
        var repo = new EmailRepository(_factory);
        await repo.UpsertEmailAsync(MakeEmail(1, "Quarterly invoice", "Please find the attached invoice for payment"));

        var hits = await MatchAsync("invoice");

        Assert.Contains(1u, hits);
    }

    [Fact]
    public async Task Insert_NonMatchingTerm_NotFound()
    {
        var repo = new EmailRepository(_factory);
        await repo.UpsertEmailAsync(MakeEmail(1, "Quarterly invoice", "Payment details inside"));

        var hits = await MatchAsync("zzzznonexistent");

        Assert.Empty(hits);
    }

    [Fact]
    public async Task Update_SyncsFts_NewTermFoundOldTermGone()
    {
        var repo = new EmailRepository(_factory);
        await repo.UpsertEmailAsync(MakeEmail(1, "Original subject", "alpha content"));

        // Upsert with new text triggers the AFTER UPDATE sync.
        await repo.UpsertEmailAsync(MakeEmail(1, "Original subject", "bravo content"));

        Assert.Contains(1u, await MatchAsync("bravo"));
        Assert.Empty(await MatchAsync("alpha"));
    }

    [Fact]
    public async Task Delete_RemovesFromFts()
    {
        var repo = new EmailRepository(_factory);
        await repo.UpsertEmailAsync(MakeEmail(1, "Removable", "deletable content"));
        Assert.Contains(1u, await MatchAsync("deletable"));

        await using (var conn = await _factory.OpenAsync())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM data_emails WHERE account = @a AND uid = @u";
            cmd.Parameters.AddWithValue("@a", "user@example.com");
            cmd.Parameters.AddWithValue("@u", 1L);
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.Empty(await MatchAsync("deletable"));
    }
}
